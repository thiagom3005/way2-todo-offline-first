# Contrato de sincronização v1

> Design proposto para o desafio; ainda não é implementação. O contrato usa HTTP/JSON, UUIDs e revisão monotônica **por usuário**.

## Convenções

- Base URL: `/api`.
- Rotas `/sync/*` exigem `Authorization: Bearer <access-token>` válido e `UserId` **extraído do token**, nunca confiado do payload.
- Tarefa é identificada por UUID criado no cliente (offline compatível).
- `dueDate`: data civil `YYYY-MM-DD` ou `null` (sem problemas de fuso horário).
- `createdAt`/`updatedAt`/`deletedAt`: instantes ISO 8601 UTC; servidor registra `updatedAt/deletedAt`. `createdAt` pode ser originalmente gerado no cliente offline.
- Categorias: campo `category` não vazio; agrupamento simples por string. Sem entidade independente neste MVP.
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
- `conflict`: a `baseVersion` não corresponde à versão atual. Inclui snapshot atual da tarefa, com tombstone se aplicável.
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

## 3. Resolução de conflito pelo usuário

Quando `push` retornar `conflict`, manter a operação original separada e abrir diálogo:

- **Usar servidor:** remover a intenção pendente do `taskId`, aplicar `serverTask` e limpar conflito.
- **Manter minha alteração:** gerar **novo** `operationId`; usar `baseVersion = serverTask.version`, `payload` igual ao snapshot local desejado (ou `delete`), e enfileirar para sincronizar. A decisão é explícita; nunca retry cego com nova baseVersion.
- Se houve uma alteração remota *posterior* à resolução, nova tentativa pode retornar outro conflito; repetir o fluxo.
- Em conflito com tombstone, explicar que a tarefa foi removida em outro dispositivo; recriação com novo `taskId` pode ser oferecida como opção em evolução futura.

## 4. Cliente: máquina de estados

- `offline`: API não alcançável; UI continua gravando no IndexedDB.
- `pending`: há operações locais aguardando envio.
- `syncing`: push/pull em andamento.
- `synced`: nenhuma operação pendente e pull concluído.
- `conflict`: é necessária decisão do usuário; não afirmar que tudo está sincronizado.
- `auth_required`: sessão expirada; permitir edição local do usuário previamente autenticado, impedir requisições até reautenticação.
- `error`: falha inesperada, com retry possível.

O estado `online` do navegador **não** prova conectividade com o backend. O indicador deve representar o resultado real de tentativas HTTP.

## 5. Tratamento de edições durante o envio

- Enquanto uma operação está `inflight`, seu `operationId` e `payload` ficam **congelados**.
- Se o usuário altera a mesma tarefa durante a requisição, gravar novo intento `pending` (não alterar a cópia em voo) e manter UI exibindo o snapshot mais recente.
- Quando o primeiro ack chega, seu `serverTask.version` passa a ser a `baseVersion` do próximo intento; o ack não pode substituir os novos valores locais da UI.
- Enviar o próximo intento em seguida. Caso servidor tenha mudado por outro dispositivo, resolver conflito.
- Depois de timeout sem resposta, enviar a **mesma** operação in-flight com o **mesmo** ID.

## 6. Integração externa: webhook

O backend cria um evento de integração na Outbox durante o commit da tarefa. Um único `BackgroundService` tenta entregar em ordem por tarefa.

`POST /webhooks/todo-events` (consumidor simulado)

```json
{
  "eventId": "ee47a272-8da7-41d7-b923-1e23c6e567f8",
  "eventType": "TaskUpdated",
  "occurredAt": "2026-10-28T12:15:00Z",
  "taskId": "8f719f2d-97d2-490c-84af-bf7996c4bc0c",
  "taskVersion": 5,
  "ownerId": "44a9729e-53c0-46f6-96db-36ae8033bd88",
  "task": {
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

- `eventType`: `TaskCreated`, `TaskUpdated` ou `TaskDeleted`.
- Consumidor retorna `2xx` mesmo para `eventId` já recebido, sem registrar evento de negócio duplicado.
- `4xx/5xx`, timeout ou indisponibilidade: Outbox mantém evento, faz retry com backoff exponencial (por exemplo 2s → 4s → 8s, máximo de 5min) e logs; sem descartar definitivamente um evento sem alerta/intervenção.
- **At-least-once**, não exactly-once. Se o POST foi recebido mas a confirmação se perdeu, ocorrerá retry e o consumidor deduplicará.
- Para a prova, um worker único entrega eventos sequencialmente. Não promover eventos posteriores da mesma tarefa se evento anterior ainda estiver pendente. Ordenação entre tarefas diferentes não é requisito.
- Em ambiente real, autenticar webhook com assinatura HMAC ou mTLS conforme contrato, limitar exposição de dados e segredos.

## 7. Cenários de teste de contrato

| Cenário | Esperado |
|---|---|
| Criar offline e reconectar | Um `TaskCreated` no consumidor e tarefa em duas sessões após pull. |
| HTTP falha depois do commit | Mesmo `operationId` responde `duplicate`, sem outro `TaskCreated`. |
| Dois dispositivos alteram `version=2` | Primeiro `applied`, segundo `conflict` e não sobrescreve o primeiro. |
| Criar e apagar antes do primeiro push | Nenhuma operação/evento no servidor, tarefa não aparece. |
| Editar durante push em voo | Novo intento fica pendente e dados locais recentes não se perdem. |
| Deletar offline | Tombstone sincronizado; outro cliente retira tarefa no pull. |
| Consumidor fora do ar | Evento permanece Outbox e chega quando retorna; sem perda. |
| JWT expirado durante offline | Cliente continua local; indicador `auth_required` para retomar sincronização. |
| Usuário B tenta operar task do A | Rejeição sem expor dados do A. |
| Pull retorna uma página e fecha navegador | Ao reabrir, cursor corresponde apenas ao que foi aplicado localmente. |
