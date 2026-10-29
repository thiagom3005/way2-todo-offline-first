import { type SyncApi, SyncHttpError } from './sync-contract';
import type { SyncStore } from './sync-store';

export type SyncRunResult =
  | { kind: 'ok'; pushed: number; pulled: number }
  | { kind: 'offline' | 'auth' | 'error'; error: unknown }
  | { kind: 'aborted' };

export interface SyncEngineOptions {
  /** Operações por requisição de push (limite do contrato: 50). */
  batchSize: number;
  pullLimit: number;
  /** Limites por execução: mantêm cada execução curta; o restante fica para a próxima. */
  maxPushRounds: number;
  maxPullPages: number;
}

export const DEFAULT_SYNC_OPTIONS: SyncEngineOptions = {
  batchSize: 50,
  pullLimit: 200,
  maxPushRounds: 10,
  maxPullPages: 50,
};

/**
 * Uma execução de sincronização: sonda a API, envia a fila (push antes do pull) e aplica as mudanças
 * remotas. Não decide quando rodar nem garante exclusividade — isso é do `SyncService`.
 */
export class SyncEngine {
  private readonly options: SyncEngineOptions;

  constructor(
    private readonly store: SyncStore,
    private readonly api: SyncApi,
    options: Partial<SyncEngineOptions> = {},
  ) {
    this.options = { ...DEFAULT_SYNC_OPTIONS, ...options };
  }

  async run(signal?: AbortSignal): Promise<SyncRunResult> {
    try {
      // Sem API, nada é congelado: as pendências continuam coalescendo enquanto offline.
      await this.api.health();
      const deviceId = await this.store.deviceId();

      let pushed = 0;
      for (let round = 0; round < this.options.maxPushRounds; round++) {
        signal?.throwIfAborted();
        // Uma operação por tarefa por rodada: a seguinte depende do ack (rebase da baseVersion).
        const batch = await this.store.claimBatch(this.options.batchSize);
        if (batch.length === 0) break;

        const { results } = await this.api.push({ deviceId, operations: batch });
        const byOperation = new Map(results.map((result) => [result.operationId, result]));
        for (const operation of batch) {
          const result = byOperation.get(operation.operationId);
          if (!result) {
            // Sem resposta para algo enviado: a operação continua inflight e será reenviada igual.
            throw new SyncHttpError('server', 'Resposta do push sem resultado para uma operação.');
          }
          await this.store.applyResult(result);
        }
        pushed += batch.length;
      }

      // Pull só com a fila confirmada: nenhuma tarefa com operação em voo tem a versão remota pulada.
      if (await this.store.hasInflight()) {
        throw new SyncHttpError('server', 'Operações ainda sem confirmação após o push.');
      }
      let pulled = 0;
      for (let page = 0; page < this.options.maxPullPages; page++) {
        signal?.throwIfAborted();
        const cursor = await this.store.cursor();
        const response = await this.api.pull(cursor, this.options.pullLimit);
        await this.store.applyPullPage(response, cursor);
        pulled += response.changes.length;
        if (!response.hasMore) break;
      }
      return { kind: 'ok', pushed, pulled };
    } catch (error) {
      if (signal?.aborted) return { kind: 'aborted' };
      if (error instanceof SyncHttpError) {
        const kind =
          error.kind === 'unreachable' ? 'offline' : error.kind === 'auth' ? 'auth' : 'error';
        return { kind, error };
      }
      return { kind: 'error', error };
    }
  }
}
