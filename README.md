# Way2 — Todo List Offline-First

> Desafio técnico *Aplicação Todo List Offline-First* (Way2 Technology).
>
> **Estado atual: Fases A (fundação), B (offline e sincronização) e C (integração externa por webhook com Transactional Outbox) concluídas.** A entrega final (Fase D) **ainda não foi feita**.

## O que funciona hoje e o que falta

| Recurso | Estado | Como foi validado |
|---|---|---|
| PostgreSQL 17 via Docker Compose, migrations EF Core | Implementado | Migrations aplicadas no compose e em Testcontainers |
| Login JWT com usuários de demonstração (senhas só como hash) e isolamento por usuário | Implementado | Testes de integração; usuário B não lê nem altera tarefas de A |
| `POST /api/sync/push`: uma transação por operação, `baseVersion`, idempotência por `operationId` + `request_hash`, `processed_operations`, `task_change_log` com tombstones, cursor monotônico por usuário | Implementado | 16 testes de integração com PostgreSQL real: duplicados (inclusive em paralelo), conteúdo diferente com o mesmo id, conflitos, tombstones, revisões contíguas, dois usuários com o mesmo id |
| `GET /api/sync/pull` paginado | Implementado | Paginação sem perda nem repetição, inclusive com pushes concorrentes |
| Angular: lista, formulário, filtros, agrupamento, seção **Urgentes** (regra calculada no cliente, funciona offline) | Implementado | Testes Vitest + teste manual |
| IndexedDB por usuário (Dexie, durabilidade `strict`): tarefa + mutação na mesma transação, coalescência, logout preservando dados | Implementado | Testes com falha injetada; as 5 pendências reais da Fase A sincronizaram sem migração |
| `SyncEngine`: sonda real da API, push antes do pull, `inflight` congelado, rebase no ack, ack e página de pull atômicos, retry com backoff, Web Locks entre abas | Implementado | 19 cenários com servidor em memória (timeout depois do commit, falha no ack, edição durante o envio, dois dispositivos, interrupção, paginação) + validação no navegador real |
| Indicador `offline` / `pending` / `syncing` / `synced` / `conflict` / `error` / `auth_required` | Implementado | Testes + navegador (API parada com `navigator.onLine` ainda `true`; sessão expirada) |
| Resolução de conflitos: "Usar versão do servidor" / "Manter minha alteração" (novo `operationId`, `baseVersion` = versão do servidor); exclusões sem ressuscitar tarefas | Implementado | 12 testes de resolução + conflito real entre dois dispositivos no navegador |
| PWA (Angular Service Worker): abrir e recarregar **sem conexão** após o primeiro carregamento | Implementado | Build de produção com nginx parado: Edge 156 headless recarregou o app pelo Service Worker ([script](scripts/verify-pwa-offline.mjs)) |
| Transactional Outbox: `task.created` / `task.updated` / `task.deleted` gravados no **mesmo commit** do push, só para `applied` | Implementado | 6 testes: um evento por mudança com o snapshot do change log, nenhum em `duplicate`/`conflict`/`validation_error`, rollback forçado sem evento, pushes paralelos; conferido no PostgreSQL real |
| `OutboxDispatcher` (`BackgroundService` + `IHttpClientFactory`): at-least-once, ordem estrita por tarefa, backoff 2s→5min, timeout, assinatura HMAC nova por tentativa, retomada após reinício | Implementado | 14 testes (500, timeout, 3xx/4xx, backoff até 200 tentativas, ordem com evento anterior em backoff/retry/reserva — com teste de mutação, dois dispatchers concorrentes, reserva expirada, reinício, falha de banco) + demonstração real |
| Consumidor webhook simulado (container separado): HMAC, dedupe por `eventId`, log JSONL com fsync, projeção com guarda de versão, falhas simuladas | Implementado | 8 testes + 2 de ponta a ponta (API real → consumidor real) + demonstração real com Docker Compose |
| Monitoramento `GET /api/integrations/outbox` (só os eventos do próprio usuário) e logs estruturados | Implementado | Teste de isolamento + uso na demonstração |

## Pré-requisitos

- .NET SDK 10.0.x
- Node.js 24.x (testado com 24.14) e npm 11
- Docker Desktop com Compose v2 (banco, API, frontend de produção e testes de integração)

O frontend usa **Angular 21.2 (LTS)**, porque o Angular 22 exige Node ≥ 24.15; com o Node atualizado, a migração é um `ng update`. `@angular/service-worker` está na mesma versão do `@angular/core` (21.2.25).

## Como executar

### Tudo em Docker (build de produção, com Service Worker)

```bash
docker compose up --build -d
```

| Serviço | Endereço |
|---|---|
| App (nginx com o build de produção e proxy `/api`) | `http://localhost:4280` |
| API | `http://localhost:5080` (aplica migrations e cria os usuários de demonstração ao iniciar) |
| PostgreSQL | `localhost:5433` (porta 5433 para não colidir com uma instalação local na 5432) |
| Consumidor webhook simulado (painel, `GET /events`, `GET /tasks`, `PUT/DELETE /failure-mode`) | `http://127.0.0.1:5090` (publicado só em 127.0.0.1) |

O Service Worker só é registrado no build de produção (`localhost` conta como contexto seguro). Depois do primeiro carregamento com rede, o app abre e recarrega mesmo com o servidor fora do ar; as tarefas vêm do IndexedDB.

### Desenvolvimento

```bash
docker compose up -d db api
```

```bash
cd src/frontend/todo-web
npm ci
npm start
```

O dev server (`http://localhost:4200`) encaminha `/api` para `http://localhost:5080` (`proxy.conf.json`) e **não** registra Service Worker. Alternativa à API em container: `docker compose stop api` e `dotnet run --project src/backend/Way2.Todo.Api` (ambiente `Development`, mesma porta).

No Windows, pare o `npm start` antes de rodar `npm ci` de novo. O dev server mantém o `esbuild.exe` aberto e o `npm ci` falha com `EPERM`, mas só depois de já ter apagado parte do `node_modules`. O servidor, ainda rodando, recompila sem o TypeScript e o navegador mostra `TS6053: File '.../node_modules/typescript/lib/lib.es2022.full.d.ts' not found`. Para corrigir, pare o servidor, rode `npm ci` e inicie `npm start` de novo.

### Usuários de demonstração

| E-mail | Senha |
|---|---|
| `demo@way2.local` | `Way2Demo!2026` |
| `demo2@way2.local` | `Way2Demo!2026` |

São valores **apenas de desenvolvimento**, definidos em `src/backend/Way2.Todo.Api/appsettings.Development.json` e nos padrões do `docker-compose.yml` (sobrescreva com `DEMO_PASSWORD`). Em `appsettings.json` o seed vem desligado e não há chave JWT.

## Roteiro de demonstração

Cada origem é um "dispositivo", com IndexedDB, sessão e Service Worker próprios. Com o compose no ar, abra **A** em `http://localhost:4280` e **B** em `http://127.0.0.1:4280` e entre com o mesmo usuário nos dois.

1. **Entre dispositivos:** crie uma tarefa em A; B recebe no próximo gatilho (foco, visibilidade ou ciclo de 30s).
2. **Offline:** `docker compose stop api`. O indicador mostra *Sem conexão com o servidor* (mesmo com `navigator.onLine` = `true`). Criar, editar, concluir e excluir continuam funcionando, e "urgente" sobe na hora.
3. **Recarga sem servidor:** `docker compose stop web` e recarregue a página. O app abre pelo Service Worker, com os dados locais.
4. **Volta da conexão:** `docker compose start api web`. A sincronização retoma sozinha com o app aberto (backoff de 1s a 60s, foco ou ciclo periódico), e o outro dispositivo recebe.
5. **Conflito:**
   - Com a API parada, edite a mesma tarefa em A e B e religue a API.
   - Quem sincroniza primeiro vence (`applied`); o outro mostra o painel de conflitos com as duas versões lado a lado.
   - "Manter minha alteração" envia uma nova operação (novo `operationId`, `baseVersion` = versão do servidor); "Usar versão do servidor" descarta a intenção local.
   - Para exclusão contra edição, as opções são "Aceitar exclusão" ou "Recriar como nova tarefa".
6. **Sessão expirada:** o app continua editando localmente e mostra *Entre novamente para sincronizar*. O logout preserva as pendências.

**Integração externa** (abra também o painel do consumidor em `http://127.0.0.1:5090`):

7. **Offline → servidor → consumidor:** `docker compose stop api` e crie uma tarefa em A. Depois `docker compose start api`: o app sincroniza sozinho, o push grava `task.created` na outbox no mesmo commit e o consumidor recebe em menos de 1s.
8. **Consumidor falhando:**
   - Ligue a falha:
     ```bash
     curl -X PUT http://127.0.0.1:5090/failure-mode -H "Content-Type: application/json" -d "{\"statusCode\":503}"
     ```
   - Edite a tarefa. O app continua *Tudo sincronizado*; os logs da API (`docker compose logs -f api`) e o `GET /api/integrations/outbox` mostram as tentativas com backoff de 2s, 4s, 8s e assim por diante.
   - Desligue a falha: o próximo retry entrega.
     ```bash
     curl -X DELETE http://127.0.0.1:5090/failure-mode
     ```
9. **Ack perdido:**
   - Ligue a falha só para a próxima entrega, depois do processamento:
     ```bash
     curl -X PUT http://127.0.0.1:5090/failure-mode -H "Content-Type: application/json" -d "{\"statusCode\":500,\"remaining\":1,\"afterProcessing\":true}"
     ```
   - Conclua a tarefa. O consumidor grava e responde 500; a API reenvia o mesmo `eventId`, e o consumidor responde `duplicate: true`. São 2 entregas, com um único efeito.
10. **Reinícios:** `docker compose stop consumer`, edite, `docker compose restart api` e `docker compose start consumer`. Os eventos pendentes são entregues, e o consumidor mantém o histórico (volume `consumer-data`).

Verificação automatizada do PWA offline, num Chromium headless com perfil temporário:

```bash
DEMO_PASSWORD=Way2Demo!2026 node scripts/verify-pwa-offline.mjs
```

O script confere se o Service Worker está ativo e controlando a página. Depois para **de verdade** os containers `web` e `api`, recarrega, cria, edita, conclui e exclui offline, religa os containers e espera a sincronização automática. Usa Edge ou Chrome instalados, ou o caminho em `BROWSER`.

## Testes

```bash
dotnet test Way2.Todo.slnx
```

São 81 testes: 29 unitários (incluindo os do consumidor, que sobe em memória) e 52 de integração com PostgreSQL via Testcontainers, que exigem Docker em execução. Os de integração incluem dois de ponta a ponta: a API real, com o dispatcher em segundo plano, entregando ao consumidor real. Sem Docker, rode só os unitários:

```bash
dotnet test Way2.Todo.slnx --filter "Category!=Integration"
```

Frontend (IndexedDB em memória com `fake-indexeddb`) e build de produção:

```bash
cd src/frontend/todo-web
npm test -- --watch=false
npm run build
```

Os testes de sincronização do frontend usam um servidor em memória com as regras do contrato (`core/sync/testing/`, só para testes). Ele permite simular, no momento exato, timeout depois do commit, API fora do ar, 401 e dois dispositivos.

Resultados da última execução (29/10/2026): backend 81/81, frontend 148/148, build de produção sem avisos, `verify-pwa-offline` aprovado.

## Configuração

| Variável (API) | Uso |
|---|---|
| `ConnectionStrings__Todo` | Conexão PostgreSQL |
| `Jwt__SigningKey` | Chave HMAC, **obrigatória** (mínimo 32 bytes); a API não sobe sem ela |
| `Jwt__Issuer`, `Jwt__Audience`, `Jwt__AccessTokenMinutes` | Padrões em `appsettings.json` (token de 8h) |
| `Database__ApplyMigrationsOnStartup` | Aplica migrations ao iniciar (dev/demo) |
| `DemoSeed__Enabled`, `DemoSeed__Users__N__Email`, `DemoSeed__Users__N__Password` | Usuários de demonstração (desligado por padrão) |
| `Webhook__Url`, `Webhook__Secret` | Destino dos webhooks e segredo HMAC (mínimo 32 bytes, validado no startup). Sem URL, os eventos ficam pendentes na outbox. |
| `Webhook__TimeoutSeconds` | Timeout por requisição (padrão 10) |
| `Outbox__PollIntervalSeconds`, `Outbox__BatchSize`, `Outbox__LeaseSeconds`, `Outbox__BaseBackoffSeconds`, `Outbox__MaxBackoffSeconds`, `Outbox__DispatcherEnabled` | Dispatcher (padrões 2s, 20, 30s, 2s, 300s, ligado) |
| `Consumer__WebhookSecret`, `Consumer__DataPath` (consumidor) | Segredo compartilhado e caminho do log JSONL |

O `docker-compose.yml` lê `POSTGRES_*`, `API_PORT`, `WEB_PORT`, `CONSUMER_PORT`, `JWT_SIGNING_KEY`, `WEBHOOK_SECRET`, `DEMO_SEED_ENABLED` e `DEMO_PASSWORD` de um `.env` opcional (veja `.env.example`). Os padrões são **apenas de desenvolvimento**.

## Endpoints

| Método | Rota | Autenticação | Descrição |
|---|---|---|---|
| `POST` | `/api/auth/login` | — | Retorna `accessToken`, `expiresAt` e `user`; 401 genérico para credenciais inválidas |
| `GET` | `/api/health` | — | Conectividade com o banco; usada pelo `SyncEngine` como sonda real |
| `POST` | `/api/sync/push` | Bearer | 1–50 operações; resultado por operação: `applied`, `duplicate`, `conflict` ou `validation_error` |
| `GET` | `/api/sync/pull?cursor=N&limit=M` | Bearer | Mudanças do usuário com `revision > N` (`limit` padrão 100, máximo 500), incluindo tombstones |
| `GET` | `/api/tasks` | Bearer | Tarefas ativas do usuário (leitura auxiliar/diagnóstico) |
| `GET` | `/api/integrations/outbox` | Bearer | Entrega dos webhooks **do próprio usuário**: pendentes, em retry, entregues, `oldestPendingAgeSeconds`, `blockedTasks` e últimos eventos |
| `POST` | `/webhooks/todo-events` (consumidor) | HMAC | Recebe o evento v1 (`task.created`, `task.updated`, `task.deleted`) |

O contrato completo, com as decisões de implementação e as garantias de entrega dos webhooks (§6), está em [`docs/SYNC_CONTRACT.md`](docs/SYNC_CONTRACT.md).

## Estrutura

```text
docker-compose.yml, Way2.Todo.slnx
scripts/verify-pwa-offline.mjs      # verificação real do PWA offline (Chromium headless + docker compose)
src/backend/Way2.Todo.Api/          # Identity/, Tasks/, Sync/, Integrations/, Infrastructure/ (monólito modular)
src/backend/Way2.Todo.Tests/        # xUnit: unitários + integração + ponta a ponta com o consumidor
src/consumer/Way2.Todo.Consumer/    # consumidor webhook simulado (container separado)
src/frontend/todo-web/              # Angular: core/auth, core/offline, core/sync, features/tasks; Dockerfile + nginx.conf
docs/                               # contrato de sincronização e diagramas
```

## Decisões e premissas

Este é um resumo; os detalhes estão nas notas **Implementado:** de [`docs/SYNC_CONTRACT.md`](docs/SYNC_CONTRACT.md).

- **Regras da lista:**
  - **Urgente:** palavra inteira `urgente` no título ou na descrição, sem diferenciar caixa; `urgentemente` e `urgentes` não contam, e `não urgente` conta (a regra é literal). É calculada no cliente.
  - **Ordenação:** urgentes primeiro, depois vencimento crescente (sem vencimento por último), criação e id. Os filtros são aplicados antes.
  - **`dueDate`** é data civil (`YYYY-MM-DD`), nunca convertida para UTC.
- **Dados locais:**
  - **IndexedDB é a fonte da UI**, inclusive online. A escrita grava tarefa e mutação na mesma transação; o servidor só recebe dados pelo `POST /api/sync/push`.
  - **Logout** remove só a sessão; dados e pendências locais do usuário são preservados.
- **Idempotência e ordem:**
  - **Idempotência:** o lock do contador do usuário vem **antes** da checagem de duplicidade, para que retries concorrentes recebam `duplicate`.
  - **Conteúdo da operação:** `request_hash` impede reutilizar um `operationId` com outro conteúdo.
  - **Conflitos** não ficam gravados, então o retry é reavaliado.
  - **Fila:** uma operação por tarefa por rodada (a seguinte depende do ack), e nada é congelado sem antes confirmar que a API responde.
- **Conflitos:** nunca há sobrescrita silenciosa. "Manter" sobre tarefa excluída no servidor recria o conteúdo como **nova** tarefa, e o registro excluído não volta.
- **Integração externa:**
  - **Quando o evento nasce:** só no commit que aplica a mudança, e nunca para `duplicate`, `conflict` ou `validation_error`.
  - **Entrega:** at-least-once, com despacho **estritamente ordenado por tarefa** (nenhum evento sai enquanto houver um anterior da mesma tarefa pendente, mesmo em backoff, em retry ou reservado).
  - **Retry:** sem descarte automático, com backoff limitado.
  - **Assinatura:** HMAC nova a cada tentativa.
  - **Consumidor:** deduplica por `eventId` e aplica só versões mais novas.
- **Sem microsserviços nem broker:** monólito modular com um projeto de API e um de testes. A outbox vive no PostgreSQL já existente. O nginx só serve o build de produção, e o consumidor é apenas um serviço de demonstração.

## Riscos e limitações atuais

- **Só com o app aberto:** a sincronização roda enquanto o app está aberto (gatilhos e backoff); **não** há Background Sync, e nada sincroniza com o navegador fechado.
- **Durabilidade do IndexedDB:** `chromeTransactionDurability: 'strict'` reduz, mas não elimina, o risco de perder a última escrita numa queda do aparelho; o suporte varia entre navegadores. A recuperação se apoia em transações, retries com o mesmo `operationId` e idempotência no servidor.
- **Web Locks:** só existe em contexto seguro (HTTPS/localhost). Num celular acessando por HTTP na rede local, vale apenas o mutex da aba; acks repetidos são inofensivos e o servidor deduplica.
- **Validação do Service Worker:** o navegador embutido usado durante o desenvolvimento não registra Service Workers (`NG05604`: "An unknown error occurred when fetching the script", até num registro manual), então a recarga offline foi validada com Edge 156 headless ([script](scripts/verify-pwa-offline.mjs)). Atualizações do app chegam pelo Service Worker na recarga seguinte; não há aviso de "nova versão" na tela.
- **Segurança local:** IndexedDB e `localStorage` (sessão) não são criptografados. Também não há refresh token: após 8h é preciso um novo login online para sincronizar, mas a edição local continua.
- **Histórico e operação:** o change log não tem retenção nem snapshot inicial, um cliente novo baixa todo o histórico do usuário. O rate limit de login é em memória, e as migrations no startup são conveniência de dev/demo.
- **Webhooks:**
  - **Entrega:** at-least-once, nunca exactly-once. A ordem de *chegada* depende da guarda de versão do consumidor (um timeout pode atrasar uma tentativa anterior).
  - **Erro permanente:** um evento com erro permanente (por exemplo, 400 recorrente) bloqueia só a sua tarefa até haver intervenção, documentada no SYNC_CONTRACT §6. Não há dead letter automático.
  - **Histórico:** mudanças anteriores à Fase C não geram eventos (sem backfill).
  - **Consumidor de demonstração:** guarda o log em arquivo, e seus endpoints de consulta e de falha não têm autenticação (só `127.0.0.1`).
  - **Várias instâncias:** o dispatcher é seguro com mais de uma instância (`SKIP LOCKED` + `claim_token`), mas depende de relógios sincronizados (NTP).

## Próximo passo (Fase D)

1. Revisão final do README e dos diagramas, com o roteiro de demonstração completo (offline, recarga, urgente, reconexão, conflito, webhook).
2. Ensaio da demonstração ponta a ponta a partir de um ambiente limpo (`docker compose down -v` num clone novo).
3. Revisão de segurança e das limitações conhecidas.

## Apoio do Claude Code

As fases A, B e C foram implementadas com apoio do Claude Code (Anthropic):
- revisão do planejamento e resolução de conflitos entre os documentos;
- implementação do backend, do frontend, dos testes e desta documentação.

Na Fase B, o desenho do algoritmo de sincronização passou, antes de virar código, por uma revisão independente. Ela encontrou, entre outros problemas:
- uma operação recusada poderia travar a fila;
- a durabilidade relaxada do IndexedDB poderia causar sobrescrita silenciosa;
- `delete()` apagava tarefas cuja criação estava em conflito;
- faltava registrar a versão remota no pull.

Esses pontos foram corrigidos e cobertos por testes.

Cada entrega foi verificada antes do commit com build e testes automatizados. A validação real usou o navegador embutido com dois dispositivos (`localhost` e `127.0.0.1`):
- sincronização das pendências reais da Fase A, conferida no PostgreSQL;
- API parada;
- conflitos resolvidos pela interface;
- exclusões;
- sessão expirada;
- timeout depois do commit, com reenvio do mesmo `operationId` respondido como `duplicate` e uma única aplicação no banco.

A recarga offline pelo Service Worker foi validada com Edge headless contra o build de produção.

Na Fase C, o desenho da outbox e do dispatcher também passou por revisão independente antes do código. Ela encontrou:
- uma reserva menor que o lote, que permitiria entrega fora de ordem com duas instâncias;
- dois relógios (o do app e o `now()` do PostgreSQL);
- estouro no cálculo do backoff, que derrubaria o host;
- redirect transformando POST em GET e marcando o evento como entregue;
- o sinal pós-commit podendo lançar exceção;
- durabilidade e concorrência no consumidor.

Tudo foi corrigido e testado. A ordem estrita por tarefa foi conferida com um **teste de mutação**: ao enfraquecer a regra para ignorar eventos anteriores em backoff ou reservados, o teste falha no ponto esperado.

Validação real com Docker Compose:
- tarefa criada offline, sincronizada ao voltar e entregue ao consumidor em 393 ms depois do commit;
- consumidor respondendo 503, com 4 retries em backoff e entrega na 5ª tentativa;
- ack perdido, com 2 entregas e efeito único;
- consumidor desligado sem afetar a sincronização;
- reinício da API com eventos pendentes, todos entregues depois;
- conferência no PostgreSQL de que `duplicate`, `conflict` e `validation_error` não geram eventos.

## Documentação

- [`docs/SYNC_CONTRACT.md`](docs/SYNC_CONTRACT.md): contratos HTTP, sincronização, idempotência e conflitos.
- [`docs/diagrams/`](docs/diagrams/): diagramas Mermaid (contexto e fluxo de sincronização atualizados para o código).
