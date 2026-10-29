# Contrato de sincronização v1

> O contrato usa HTTP/JSON, UUIDs e revisão monotônica **por usuário**.
>
> **Estado da implementação:** login e [endpoints auxiliares](#endpoints-auxiliares-fase-a) (Fase A), `POST /api/sync/push`, `GET /api/sync/pull`, o `SyncEngine` do cliente e a resolução de conflitos (Fase B) estão implementados e testados. A Transactional Outbox, o dispatcher de webhooks e o consumidor simulado (§6) estão implementados e testados (Fase C). Decisões de implementação estão marcadas com **Implementado:**.

## Convenções

- Base URL: `/api`.
- Rotas `/sync/*` exigem `Authorization: Bearer <access-token>` válido e `UserId` **extraído do token**, nunca confiado do payload.
- Tarefa é identificada por UUID criado no cliente (offline compatível).
- `dueDate`: data civil `YYYY-MM-DD` ou `null` (sem problemas de fuso horário).
- `createdAt`/`updatedAt`/`deletedAt`: instantes ISO 8601 UTC; servidor registra `updatedAt/deletedAt`. `createdAt` pode ser originalmente gerado no cliente offline.
- Categorias: campo `category` não vazio; agrupamento simples por string. Sem entidade independente neste MVP.
- Validação (servidor e cliente): `title` obrigatório, 1–200 caracteres após `trim`; `description` até 2000 (vazia vira `null`); `category` obrigatória, 1–50 após `trim` (o formulário sugere `Geral`); `createdAt` deve ser instante UTC. Chaves de erro usam os nomes JSON (`title`, `category`, ...).
- Enumeração `priority`: `low` | `medium` | `high`. `status`: `pending` | `completed`.
- Os exemplos usam UUIDs e instantes ilustrativos, **não são dados reais**.

## Autenticação

`POST /api/auth/login`

```json
{
  "email": "demo@way2.local",
  "password": "<senha-configurada-no-seed>"
}
```

Resposta `200`:

```json
{
  "accessToken": "<JWT>",
  "expiresAt": "2026-10-28T19:00:00Z",
  "user": {
    "id": "44a9729e-53c0-46f6-96db-36ae8033bd88",
    "email": "demo@way2.local"
  }
}
```

Credenciais finais de teste devem estar descritas no README da aplicação. Primeira autenticação requer rede. Depois de autenticar, app usa dados locais offline, mas push/pull espera uma sessão válida.

Erros:

- `400` (`ValidationProblem`): `email` ou `password` ausentes.
- `401` (`ProblemDetails`, `title: "Credenciais inválidas."`): resposta **idêntica** para e-mail inexistente e senha errada (o custo de hash também é equalizado, para não permitir enumerar usuários).
- `429`: limite de 20 tentativas por minuto por IP.

O JWT (HMAC-SHA256) carrega `sub` (UserId) e `email`; `expiresAt` é exatamente o `exp` do token. Não há refresh token: com o token expirado o cliente continua editando localmente e mostra `auth_required` até novo login.

## Modelo de tarefa (snapshot)

```json
{
  "id": "8f719f2d-97d2-490c-84af-bf7996c4bc0c",
  "title": "Conferir medição urgente",
  "description": "Verificar unidade A",
  "dueDate": "2026-10-30",
  "priority": "high",
  "status": "pending",
  "category": "Campo",
  "version": 4,
  "createdAt": "2026-10-28T12:02:00Z",
  "updatedAt": "2026-10-28T12:10:00Z",
  "deletedAt": null
}
```

O cliente **não envia `version` como valor desejado**: envia `baseVersion` para controle concorrente. `deletedAt` aparece somente no snapshot de resultado, não é campo editável pelo cliente.

## 1. Enviar alterações locais

`POST /api/sync/push`

```json
{
  "deviceId": "d65b9960-f98a-45c3-93aa-02d164b213a1",
  "operations": [
    {
      "operationId": "3c2090bc-f25e-4515-914c-e4361c70bc8d",
      "taskId": "8f719f2d-97d2-490c-84af-bf7996c4bc0c",
      "action": "upsert",
      "baseVersion": null,
      "payload": {
        "title": "Conferir medição urgente",
        "description": "Verificar unidade A",
        "dueDate": "2026-10-30",
        "priority": "high",
        "status": "pending",
        "category": "Campo",
        "createdAt": "2026-10-28T12:02:00Z"
      }
    }
  ]
}
```

Significados:

- `action=upsert`, `baseVersion=null`: **create**. Retornar `conflict` se taskId já existe para o mesmo usuário, exceto retry com `operationId` já processado.
- `action=upsert`, `baseVersion>=1`: **update**. Requer taskId existente e versão correspondente.
- `action=delete`, `baseVersion>=1`: **soft delete**. Payload omitido ou `null`.
- `operationId`: UUID gerado quando uma intenção entra na fila. **Imutável nos retries**. Unicidade por usuário.
- Limite proposto: 50 operações por lote. Processamento na ordem recebida, uma transação por operação. Um erro de negócio em uma operação não invalida as demais, mas tarefas com operações dependentes são enviadas sequencialmente pelo cliente.

**Implementado:**

- Lote com 0 ou mais de 50 operações, ou `deviceId` vazio → `400` (`ValidationProblem`). JSON malformado também → `400`.
- O servidor despacha pela `action` antes de olhar `baseVersion`: `delete` sem `baseVersion` é `validation_error` e **nunca** vira criação.
- `priority`, `status`, `dueDate` e `createdAt` são convertidos por operação: valor inválido gera `validation_error` só naquela operação (`errors` com as chaves `priority`, `dueDate`...), e as demais seguem.
- **Identidade da operação:** `processed_operations.request_hash` guarda o SHA-256 da representação canônica (`taskId`, `action`, `baseVersion` e payload normalizado: campos após `trim`, enums em minúsculas, `dueDate` `YYYY-MM-DD`, `createdAt` UTC em ms). Um `operationId` já processado chegando com outro conteúdo (ou outra tarefa) recebe `validation_error` (`errors.operationId`) e nada é reaplicado. O mesmo conteúdo com espaços extras continua sendo `duplicate`.
- O cliente envia **uma operação por tarefa por requisição** (a seguinte depende do ack, que rebaseia a `baseVersion`); tarefas diferentes vão no mesmo lote.

Resposta `200`, mesmo quando há conflito em operação individual:

```json
{
  "results": [
    {
      "operationId": "3c2090bc-f25e-4515-914c-e4361c70bc8d",
      "status": "applied",
      "serverRevision": 21,
      "serverTask": {
        "id": "8f719f2d-97d2-490c-84af-bf7996c4bc0c",
        "title": "Conferir medição urgente",
        "description": "Verificar unidade A",
        "dueDate": "2026-10-30",
        "priority": "high",
        "status": "pending",
        "category": "Campo",
        "version": 1,
        "createdAt": "2026-10-28T12:02:00Z",
        "updatedAt": "2026-10-28T12:10:00Z",
        "deletedAt": null
      }
    }
  ]
}
```

Resultados possíveis por operação:

- `applied`: operação aceita, versão incrementada, evento de integração gravado na Outbox.
- `duplicate`: mesmo `operationId` já confirmado anteriormente. Resposta retorna **o resultado original armazenado**, sem executar mutação/emitir outro evento.
- `conflict`: operação recusada sem alterar nada, com `reason`:
  - `version_mismatch`: `baseVersion` diferente da versão atual; inclui `serverTask`.
  - `deleted`: a tarefa já é um tombstone; inclui `serverTask` com `deletedAt`.
  - `already_exists`: criação com `taskId` já usado; `serverTask` só é preenchido se a tarefa for do próprio usuário.
  - `not_found`: update/delete de tarefa inexistente **ou de outro usuário**; `serverTask` é `null` (nunca expõe dados de outra conta).
- `validation_error`: campos inválidos; não alterar o servidor, retornar detalhes de validação.
- Falhas transitórias de banco/rede respondem com `5xx` ou falha de conexão. Cliente reenvia a **mesma operação**, sem substituir `operationId`.

Exemplo `conflict` (mesma tarefa atualizada em outro dispositivo):

```json
{
  "operationId": "f38883a7-87d4-4c1f-80c0-3a2a7d63f055",
  "status": "conflict",
  "reason": "version_mismatch",
  "serverTask": {
    "id": "8f719f2d-97d2-490c-84af-bf7996c4bc0c",
    "title": "Tarefa alterada no outro dispositivo",
    "description": null,
    "dueDate": null,
    "priority": "medium",
    "status": "pending",
    "category": "Geral",
    "version": 5,
    "createdAt": "2026-10-28T12:02:00Z",
    "updatedAt": "2026-10-28T12:15:00Z",
    "deletedAt": null
  }
}
```

### Atomicidade do processamento no servidor

Na **mesma transação PostgreSQL** por operação aceita:

1. Conferir se `(userId, operationId)` já foi processada; se sim, devolver o resultado anterior.
2. Conferir permissão e `baseVersion` da tarefa sob controle concorrente.
3. Bloquear `user_sync_counters` do usuário, incrementar `last_revision`.
4. Inserir/atualizar/excluir logicamente a tarefa e incrementar `version`.
5. Inserir `task_change_log` e `integration_outbox`.
6. Gravar `processed_operations` com resultado original e confirmar transação.

**Implementado** (`Sync/SyncService.cs`), na ordem:

1. `INSERT … ON CONFLICT DO NOTHING` e `SELECT … FOR UPDATE` em `user_sync_counters`, **antes** da checagem de duplicidade. Assim um retry concorrente do mesmo `operationId` (timeout seguido de reenvio enquanto o primeiro ainda roda) espera o lock e, ao seguir, enxerga a operação já gravada (Read Committed). Um único lock por transação, sempre o primeiro: sem deadlock.
2. `processed_operations` por `(user_id, operation_id)`: mesmo `request_hash` → `duplicate` com o resultado original; outro conteúdo → `validation_error`.
3. `TaskCommandService` (create/update/delete com `baseVersion`). Em `conflict`/`validation_error`, rollback: **nada** é gravado e nenhuma revisão é consumida; o retry reavalia e dá o mesmo resultado (versões só crescem e tombstones são permanentes).
4. Em `applied`: `last_revision + 1`, `task_change_log` com o snapshot e `processed_operations` com `{serverRevision, serverTask}`, no mesmo commit da tarefa.
5. A `integration_outbox` é gravada nesta mesma transação (Fase C, §6).

Falha transitória devolve `5xx` para o lote inteiro; o cliente reenvia, e o que já tinha sido commitado volta como `duplicate`. A migração `AddSyncTables` não gera change log para tarefas anteriores: não havia nenhuma, porque a Fase A não tinha endpoint de escrita.

Unicidade no banco e tratamento de disputa de concorrência são necessários; não confiar apenas em checagens prévias em memória. Uma nova versão de tarefa produzida por operação bem-sucedida não implica que **todas as** cópias locais do cliente possam ser substituídas por esse snapshot (pode existir edição mais nova ainda pendente).

## 2. Buscar alterações confirmadas

`GET /api/sync/pull?cursor=20&limit=100`

Resposta `200`:

```json
{
  "changes": [
    {
      "revision": 21,
      "action": "upsert",
      "task": {
        "id": "8f719f2d-97d2-490c-84af-bf7996c4bc0c",
        "title": "Conferir medição urgente",
        "description": "Verificar unidade A",
        "dueDate": "2026-10-30",
        "priority": "high",
        "status": "pending",
        "category": "Campo",
        "version": 1,
        "createdAt": "2026-10-28T12:02:00Z",
        "updatedAt": "2026-10-28T12:10:00Z",
        "deletedAt": null
      }
    },
    {
      "revision": 22,
      "action": "delete",
      "task": {
        "id": "91d6585e-8f97-4c85-b15c-a0181b218033",
        "title": "Registro removido",
        "description": null,
        "dueDate": null,
        "priority": "low",
        "status": "completed",
        "category": "Geral",
        "version": 8,
        "createdAt": "2026-10-27T08:00:00Z",
        "updatedAt": "2026-10-28T12:12:00Z",
        "deletedAt": "2026-10-28T12:12:00Z"
      }
    }
  ],
  "nextCursor": 22,
  "hasMore": false
}
```

- `cursor=0`: primeira sincronização. O servidor devolve mudanças **apenas do usuário autenticado**, ordenadas por `revision ASC`.
- `nextCursor`: revisão do **último item enviado**, ou mesmo cursor da requisição quando não há alterações.
- `hasMore=true`: cliente pagina até a última resposta; não avançar além das mudanças efetivamente aplicadas localmente.
- Cursor e alterações locais são gravados na mesma transação IndexedDB; em erro, replay é seguro.
- Snapshot com `deletedAt` remove a tarefa da listagem, mas mantém tombstone para reconciliação.
- Se existe uma edição local pendente para `taskId`, **não sobrescrever a cópia de trabalho** com a mudança remota; registrar a versão remota para futura resolução.
- Para a prova, o histórico é mantido sem expiração; uma solução de produção poderia adicionar snapshot inicial, retenção e política de reset de cursor.

**Implementado:**

- `limit`: padrão 100, máximo 500; `cursor < 0` ou `limit` fora do intervalo → `400`. O cliente pede páginas de 200.
- Revisões de um usuário são atribuídas sob o lock do contador e confirmadas em ordem, então o pull nunca vê a revisão N sem as anteriores.
- O cliente só faz pull quando não há operação `inflight` e aplica cada página e o cursor na **mesma** transação IndexedDB.
- Tarefa com mutação local não tem a cópia de trabalho tocada: o snapshot remoto mais novo fica em `local_tasks.remoteSnapshot` e é usado no ack e na resolução de conflito.
- Mudança com `version ≤ serverVersion` local é ignorada: o replay é seguro e não ressuscita tarefas excluídas.

## 3. Resolução de conflito pelo usuário

Quando `push` retornar `conflict`, manter a operação original separada e abrir diálogo:

- **Usar servidor:** remover a intenção pendente do `taskId`, aplicar `serverTask` e limpar conflito.
- **Manter minha alteração:** gerar **novo** `operationId`; usar `baseVersion = serverTask.version`, `payload` igual ao snapshot local desejado (ou `delete`), e enfileirar para sincronizar. A decisão é explícita; nunca retry cego com nova baseVersion.
- Se houve uma alteração remota *posterior* à resolução, nova tentativa pode retornar outro conflito; repetir o fluxo.
- Em conflito com tombstone, explicar que a tarefa foi removida em outro dispositivo; recriação com novo `taskId` pode ser oferecida como opção em evolução futura.

**Implementado** (`core/sync/sync-store.ts`, `features/tasks/conflict-panel.ts`):

- A mutação recusada vira `status: 'conflict'` e bloqueia só a fila daquela tarefa; o painel mostra a cópia de trabalho atual × o servidor mais recente conhecido.
- Edições feitas com o conflito aberto vão para a cópia de trabalho e seguem juntas em "Manter".
- Cada decisão é uma transação IndexedDB e só vale para o conflito exibido: outro `operationId` dá `stale`, e cópia alterada depois de exibida dá `changed` (o usuário revisa de novo). A decisão nunca descarta mutação `inflight`.
- Por motivo:

| Motivo | "Usar versão do servidor" | "Manter minha alteração" |
|---|---|---|
| `version_mismatch`, `already_exists` (tarefa própria) | Aplica o servidor e descarta as intenções | Uma nova operação: novo `operationId`, `baseVersion` = versão do servidor, payload = cópia local (ou `delete`) |
| `deleted` | "Aceitar exclusão" | "Recriar como nova tarefa": novo `taskId`; o original continua tombstone |
| `not_found`, `already_exists` sem `serverTask` | Descarta a cópia local | "Recriar como nova tarefa" |
| `validation_error` | Descarta a alteração | "Enviar novamente" (cópia atual, possivelmente corrigida, com novo `operationId`) |

- Resolvido sem perguntar: exclusão local contra tarefa já excluída ou inexistente no servidor (as duas pontas querem a tarefa fora).

## 4. Cliente: máquina de estados

- `offline`: API não alcançável; UI continua gravando no IndexedDB.
- `pending`: há operações locais aguardando envio.
- `syncing`: push/pull em andamento.
- `synced`: nenhuma operação pendente e pull concluído.
- `conflict`: é necessária decisão do usuário; não afirmar que tudo está sincronizado.
- `auth_required`: sessão expirada; permitir edição local do usuário previamente autenticado, impedir requisições até reautenticação.
- `error`: falha inesperada, com retry possível.

O estado `online` do navegador **não** prova conectividade com o backend. O indicador deve representar o resultado real de tentativas HTTP.

**Implementado** (`core/sync/sync.service.ts`):

- **Classificação das falhas:**
  - `GET /api/health` sem resposta, timeout, status 0, 502, 503 ou 504 → `offline`;
  - 401 ou token local expirado → `auth_required` (sem requisições até novo login);
  - outros erros → `error`.
- **Prioridade dos estados:** `auth_required` > `conflict` > `offline` > `error` > `pending` > `synced`. `syncing` aparece durante uma execução com pendências.
- **Disparos:** início, `online`, foco, `visibilitychange`, escrita local (debounce de 1s) e a cada 30s com a aba visível.
- **Falhas:** backoff exponencial de 1s até 60s com jitter de ±20%.
- **Uma execução por vez:** mutex na aba e Web Locks por usuário entre abas. Web Locks só existe em contexto seguro (HTTPS/localhost); sem ele vale só o mutex da aba, e acks repetidos continuam inofensivos.
- **Só com o app aberto:** a sincronização não usa Background Sync.

## 5. Tratamento de edições durante o envio

- Ciclo de vida de `local_mutations.status`: `pending` (nunca enviada; pode ser coalescida com edições seguintes da mesma tarefa) → `inflight` (selecionada para envio) → removida no ack. Uma mutação `inflight` **nunca volta** a `pending`, mesmo após timeout: retries reenviam exatamente a mesma operação.
- Enquanto uma operação está `inflight`, seu `operationId` e `payload` ficam **congelados**.
- Se o usuário altera a mesma tarefa durante a requisição, gravar novo intento `pending` (não alterar a cópia em voo) e manter UI exibindo o snapshot mais recente.
- Quando o primeiro ack chega, seu `serverTask.version` passa a ser a `baseVersion` do próximo intento; o ack não pode substituir os novos valores locais da UI.
- Enviar o próximo intento em seguida. Caso servidor tenha mudado por outro dispositivo, resolver conflito.
- Depois de timeout sem resposta, enviar a **mesma** operação in-flight com o **mesmo** ID.

**Implementado:**

- O congelamento `pending → inflight` acontece numa transação IndexedDB antes do POST, com `chromeTransactionDurability: 'strict'`. Isso reduz a janela em que uma queda do aparelho "desfaria" o congelamento, mas não é garantia absoluta: o suporte varia entre navegadores, e a recuperação continua apoiada em retries com o mesmo `operationId`, idempotência no servidor e conflitos explícitos.
- O ack (remover a mutação, rebasear a próxima e atualizar `serverVersion` sem nunca regredir) é uma única transação. Se falhar, a mutação continua `inflight` e o retry recebe `duplicate`.
- Reenvio de `inflight` muda só `retryCount`.
- A sonda de saúde vem antes do congelamento: offline, as edições continuam coalescendo na mesma mutação `pending`.

## 6. Integração externa: webhook (contrato de eventos v1)

**Implementado** (`Integrations/` na API e `src/consumer/Way2.Todo.Consumer`).

### Quando um evento nasce

- Na **mesma transação** do push que aplica a mudança: tarefa, `user_sync_counters`, `task_change_log`, `processed_operations` e `integration_outbox` são confirmados juntos. Se o commit falhar, não há tarefa nem evento.
- **Só** para operações `applied`. `duplicate`, `conflict` e `validation_error` retornam antes e não geram evento.
- `UNIQUE(user_id, server_revision)` garante um evento por mudança aplicada.
- Nenhuma chamada HTTP acontece dentro da transação. A entrega é assíncrona, por um `BackgroundService` (`OutboxDispatcher`) com `IHttpClientFactory`.
- Mudanças anteriores à Fase C não geram evento (sem backfill). O consumidor trata `task.updated` de tarefa desconhecida como criação, porque o snapshot é completo.

### Envelope

`POST <Webhook:Url>` (no compose: `http://consumer:8080/webhooks/todo-events`)

```json
{
  "schemaVersion": 1,
  "eventId": "01a18556-ef43-761c-85b7-6464ef446763",
  "eventType": "task.updated",
  "occurredAt": "2026-10-29T01:47:07.252Z",
  "userId": "44a9729e-53c0-46f6-96db-36ae8033bd88",
  "taskId": "8f719f2d-97d2-490c-84af-bf7996c4bc0c",
  "taskVersion": 2,
  "serverRevision": 16,
  "task": {
    "id": "8f719f2d-97d2-490c-84af-bf7996c4bc0c",
    "title": "Tarefa alterada",
    "description": null,
    "dueDate": "2026-10-30",
    "priority": "medium",
    "status": "pending",
    "category": "Geral",
    "version": 2,
    "createdAt": "2026-10-28T12:02:00Z",
    "updatedAt": "2026-10-29T01:47:07.252Z",
    "deletedAt": null
  }
}
```

**Campos:**

| Campo | Significado |
|---|---|
| `eventType` | `task.created` (criação aplicada), `task.updated` (upsert com `baseVersion`) ou `task.deleted` (exclusão lógica) |
| `task` | Snapshot completo da tarefa após a mudança, idêntico ao do `task_change_log` da mesma `serverRevision`. Em `task.deleted`, `task.deletedAt` vem preenchido (tombstone), e o consumidor remove logicamente pela `taskId`. |
| `eventId` | UUID v7, imutável: o mesmo em todas as tentativas |
| `taskVersion` | Versão da tarefa; serve de guarda de versão no consumidor |
| `serverRevision` | Revisão monotônica do usuário |
| `occurredAt` | Instante do commit |

**Evolução:** campos novos são aditivos. Uma mudança incompatível vira `schemaVersion: 2`, e o consumidor recusa versões que não conhece (o evento fica em retry até ele ser atualizado).

### Cabeçalhos e autenticação

- `Content-Type: application/json`, `X-Way2-Event-Id` e `X-Way2-Event-Type`.
- `X-Way2-Signature: t=<unix>,v1=<hex>`, com `v1 = HMAC-SHA256(segredo, "<t>." + corpo)` calculado sobre os **bytes exatos enviados**.
  - Timestamp e assinatura são **gerados de novo a cada tentativa**, mesmo depois de longa indisponibilidade; `eventId` e payload não mudam.
  - O consumidor verifica sobre o corpo cru, antes de desserializar, compara em tempo constante e aceita `t` dentro de ±5 min. Caso contrário responde **401**, que a API registra como Error, por indicar configuração errada.
- O segredo (`Webhook:Secret` / `Consumer:WebhookSecret`, mínimo de 32 bytes) vem só de configuração. No compose ele vem de `WEBHOOK_SECRET`, com padrão *dev-only*.

### Garantias de entrega

| Garantia | Como |
|---|---|
| **At-least-once** (nunca exactly-once) | O evento só vira `delivered` com resposta `2xx`. Timeout, falha de conexão ou resposta perdida geram nova entrega do mesmo `eventId`. |
| **Idempotência** | O consumidor deduplica por `eventId` e responde `2xx` com `duplicate: true` para repetições, sem repetir o efeito. |
| **Ordem por tarefa (despacho)** | Um evento só é despachado quando **não existe nenhum evento anterior da mesma tarefa** com `status = 'pending'`, esteja ele em backoff, em retry ou reservado por outro ciclo. Tarefas diferentes não se bloqueiam. A ordem de *chegada* não é garantida: um timeout pode atrasar a resposta de uma tentativa anterior. Por isso o consumidor aplica só `taskVersion` maior que a conhecida. |
| **Retry** | Backoff exponencial `2s × 2^(n-1)`, com expoente limitado, teto de 5 min e jitter de ±20%. Timeout de 10s por requisição. Redirects não são seguidos (3xx conta como falha). Toda resposta não-2xx é retentada; nada é descartado automaticamente. |
| **Falhas persistidas** | `attempts`, `last_attempt_at`, `last_status_code`, `last_error` (só o tipo: `HTTP 503`, `Timeout`, `Falha de conexão (...)`, sem URL, corpo ou segredo) e `next_retry_at`. Log de Error na 10ª tentativa e a cada 10 seguintes. |
| **Reinício** | Tudo está no PostgreSQL. A reserva (`locked_until` + `claim_token`, renovada antes de cada POST) expira e o próximo ciclo retoma. Resultado de reserva expirada é ignorado. `FOR UPDATE SKIP LOCKED` permite mais de uma instância da API. |
| **Isolamento** | O dispatcher roda fora das requisições. Consumidor fora do ar não afeta push, pull nem `/api/health`. Timeout ou falha de conexão encerram o ciclo sem gastar tentativas dos outros eventos. Erro de banco num ciclo é logado e não derruba a API. |
| **Quase tempo real** | Um sinal pós-commit (canal de capacidade 1, que nunca lança) acorda o dispatcher na hora; há polling de 2s como reserva. Na demonstração, a entrega levou 393 ms depois do commit. |

### Acompanhamento

- `GET /api/integrations/outbox` (Bearer): **só os eventos do usuário do token**.
  - Contagens: `pending`, `retrying` (pendente que já falhou) e `delivered`.
  - Saúde: `oldestPendingAgeSeconds` e `blockedTasks` (tarefas paradas num evento que já falhou).
  - Lista: os 50 eventos mais recentes, com tentativas, próximo retry, último erro e `deliveredAt`.
- Logs estruturados com `EventId`, `EventType`, `TaskId`, `Attempt`, `Result`, `ElapsedMs` e `NextRetryAt`.
- **Intervenção manual** num evento com erro permanente: depois de corrigir a causa, `UPDATE integration_outbox SET next_retry_at = now() WHERE event_id = '<id>'` antecipa o retry. Marcar como `delivered` só se o consumidor confirmar que já tem o evento.

### Consumidor simulado (somente leitura)

- `POST /webhooks/todo-events`:
  1. verifica a assinatura;
  2. aplica a falha simulada, se houver;
  3. deduplica por `eventId`;
  4. grava no log JSONL com fsync e **só então** atualiza o índice e a projeção (guarda de versão; `task.deleted` marca exclusão);
  5. responde `200 {"eventId": "...", "duplicate": false|true}`.
- Consulta: `GET /events` (eventos, entregas por `eventId`, totais e duplicatas), `GET /tasks` (projeção) e `GET /` (painel com atualização automática).
- `PUT/DELETE /failure-mode` com `{ "statusCode": 503, "remaining": 2, "delayMs": 0, "afterProcessing": false }`:
  - `afterProcessing: true` grava e só depois responde o erro, simulando ack perdido;
  - `delayMs` acima do timeout da API força timeout;
  - sem `remaining`, a falha vale até ser removida.
- O consumidor não tem credenciais de banco nem token da API, então **não consegue alterar tarefas**. Porta publicada só em `127.0.0.1:5090`; os endpoints de consulta e de falha não têm autenticação, por serem de demonstração.

## Endpoints auxiliares (Fase A)

Não fazem parte do fluxo de sincronização; existem para operação e demonstração.

- `GET /api/health` (anônimo): `200 {"status":"healthy"}` quando o PostgreSQL responde; `503` caso contrário. É a sondagem real de conectividade do `SyncEngine` antes de cada execução.
- `GET /api/tasks` (Bearer obrigatório): tarefas **ativas** do usuário do token (sem tombstones), no formato do snapshot acima, ordenadas por `createdAt`. É somente leitura: o cliente offline não o usa; toda escrita será feita via `POST /api/sync/push`, para existir um único caminho com idempotência, change log e outbox.

## 7. Cenários de teste de contrato

| Cenário | Esperado |
|---|---|
| Criar offline e reconectar | Um `task.created` no consumidor e tarefa em duas sessões após pull. |
| HTTP falha depois do commit | Mesmo `operationId` responde `duplicate`, sem outro `task.created`. |
| Dois dispositivos alteram `version=2` | Primeiro `applied`, segundo `conflict` e não sobrescreve o primeiro. |
| Criar e apagar antes do primeiro push | Nenhuma operação/evento no servidor, tarefa não aparece. |
| Editar durante push em voo | Novo intento fica pendente e dados locais recentes não se perdem. |
| Deletar offline | Tombstone sincronizado; outro cliente retira tarefa no pull. |
| Consumidor fora do ar | Evento permanece Outbox e chega quando retorna; sem perda. |
| JWT expirado durante offline | Cliente continua local; indicador `auth_required` para retomar sincronização. |
| Usuário B tenta operar task do A | Rejeição sem expor dados do A. |
| Pull retorna uma página e fecha navegador | Ao reabrir, cursor corresponde apenas ao que foi aplicado localmente. |
