# Way2 — Todo List Offline-First

> Desafio técnico *Aplicação Todo List Offline-First* (Way2 Technology).
>
> **Estado atual: Fase A (fundação) concluída.** Sincronização com o servidor (Fase B), integração externa por webhook (Fase C) e a entrega final (Fase D) **ainda não foram implementadas**. Na Fase A, tarefas criadas no navegador ficam salvas apenas no IndexedDB daquele dispositivo.

## O que funciona hoje e o que falta

| Recurso | Estado | Como foi validado |
|---|---|---|
| PostgreSQL 17 via Docker Compose, migrations EF Core (`users`, `tasks`) | Implementado | Migration aplicada no banco do compose e em Testcontainers |
| Modelo de tarefa com `version` inteira, exclusão lógica (`deleted_at`) e conflito por `baseVersion` (`TaskCommandService`) | Implementado, **ainda sem endpoint de escrita** (chega com `/api/sync/push`) | Testes de integração com PostgreSQL real: conflito, concorrência, tombstone, isolamento |
| Login JWT com usuários de demonstração (senhas só como hash) | Implementado | Testes de integração + `curl` contra o container |
| Isolamento por usuário (UserId sempre do token) | Implementado | Testes: usuário B não vê nem altera tarefas de A |
| `GET /api/health`, `GET /api/tasks` (leitura auxiliar) | Implementado | Testes de integração + `curl` |
| Angular: login, tela de tarefas, formulário, filtros, agrupamento por categoria, seção **Urgentes** | Implementado | 76 testes Vitest + teste manual no navegador |
| Regra `urgente` (palavra inteira, sem diferenciar caixa, título ou descrição) calculada no cliente | Implementado | Testes unitários e manual (sobe ao topo ao salvar) |
| IndexedDB (Dexie) por usuário: tarefa + mutação pendente na mesma transação, coalescência, logout preservando dados | Implementado | Testes com IndexedDB em memória (falha injetada, inflight congelada) + inspeção no navegador |
| Sincronização push/pull, indicador `Sincronizando`/`Sincronizado`, conflitos na UI | **Não implementado** (Fase B) | — |
| PWA / abrir e recarregar **sem rede** (Service Worker) | **Não implementado** (Fase B). Hoje os dados sobrevivem a recarga *com* rede | — |
| Transactional outbox, worker de webhook, consumidor idempotente | **Não implementado** (Fase C) | — |

O indicador da tela diz explicitamente quantas alterações estão salvas só no dispositivo e que a sincronização ainda não existe.

## Pré-requisitos

- .NET SDK 10.0.x
- Node.js 24.x (testado com 24.14) e npm 11
- Docker Desktop com Compose v2 (banco, API em container e testes de integração)

O frontend usa **Angular 21.2 (LTS)**: o Angular 22 exige Node ≥ 24.15. Com o Node atualizado, a migração é um `ng update`.

## Como executar

### 1. Banco e API (Docker)

```bash
docker compose up --build -d
```

- API: `http://localhost:5080` (aplica migrations e cria os usuários de demonstração ao iniciar)
- PostgreSQL: `localhost:5433` (porta de host 5433 para não colidir com uma instalação local na 5432)

```bash
curl http://localhost:5080/api/health
```

Alternativa com a API fora do Docker (ambiente `Development`, mesma porta 5080; pare o container da API antes com `docker compose stop api`):

```bash
docker compose up -d db
```

```bash
dotnet run --project src/backend/Way2.Todo.Api
```

### 2. Frontend

```bash
cd src/frontend/todo-web
npm ci
npm start
```

Abra `http://localhost:4200`. O dev server encaminha `/api` para `http://localhost:5080` (`proxy.conf.json`).

No Windows, pare o `npm start` antes de rodar `npm ci` de novo: o dev server mantém o `esbuild.exe` aberto e o `npm ci` falha com `EPERM` depois de já ter apagado parte do `node_modules`. O servidor, ainda rodando, recompila sem o TypeScript e o navegador mostra `TS6053: File '.../node_modules/typescript/lib/lib.es2022.full.d.ts' not found`. Para corrigir: pare o servidor, rode `npm ci` e inicie `npm start` de novo (não é preciso mudar `tsconfig` nem `package-lock.json`).

### Usuários de demonstração

| E-mail | Senha |
|---|---|
| `demo@way2.local` | `Way2Demo!2026` |
| `demo2@way2.local` | `Way2Demo!2026` |

Valores **apenas de desenvolvimento**, definidos em `src/backend/Way2.Todo.Api/appsettings.Development.json` e nos padrões do `docker-compose.yml` (sobrescreva com `DEMO_PASSWORD`). Em `appsettings.json` o seed vem desligado e não há chave JWT.

## Testes

```bash
dotnet test Way2.Todo.slnx
```

35 testes (19 unitários + 16 de integração com PostgreSQL via Testcontainers — exige Docker em execução). Sem Docker, rode só os unitários:

```bash
dotnet test Way2.Todo.slnx --filter "Category!=Integration"
```

Frontend (76 testes, IndexedDB em memória com `fake-indexeddb`) e build de produção:

```bash
cd src/frontend/todo-web
npm test -- --watch=false
npm run build
```

Resultados da última execução (28/10/2026): backend 35/35 aprovados, frontend 76/76 aprovados, build do frontend sem avisos.

## Configuração

| Variável (API) | Uso |
|---|---|
| `ConnectionStrings__Todo` | Conexão PostgreSQL |
| `Jwt__SigningKey` | Chave HMAC, **obrigatória** (mínimo 32 bytes); a API não sobe sem ela |
| `Jwt__Issuer`, `Jwt__Audience`, `Jwt__AccessTokenMinutes` | Padrões em `appsettings.json` (token de 8h) |
| `Database__ApplyMigrationsOnStartup` | Aplica migrations ao iniciar (dev/demo) |
| `DemoSeed__Enabled`, `DemoSeed__Users__N__Email`, `DemoSeed__Users__N__Password` | Usuários de demonstração (desligado por padrão) |

O `docker-compose.yml` lê `POSTGRES_*`, `API_PORT`, `JWT_SIGNING_KEY`, `DEMO_SEED_ENABLED` e `DEMO_PASSWORD` de um `.env` opcional (veja `.env.example`).

## Endpoints disponíveis

| Método | Rota | Autenticação | Descrição |
|---|---|---|---|
| `POST` | `/api/auth/login` | — | Retorna `accessToken`, `expiresAt` e `user`; 401 genérico para credenciais inválidas |
| `GET` | `/api/health` | — | Conectividade com o banco |
| `GET` | `/api/tasks` | Bearer | Tarefas ativas do usuário do token (leitura auxiliar) |

Contrato completo, incluindo o que ainda será implementado: [`docs/SYNC_CONTRACT.md`](docs/SYNC_CONTRACT.md).

## Estrutura

```text
docker-compose.yml, Way2.Todo.slnx
src/backend/Way2.Todo.Api/        # Identity/, Tasks/, Infrastructure/ (monólito modular)
src/backend/Way2.Todo.Tests/      # xUnit: unitários + integração
src/frontend/todo-web/            # Angular: core/auth, core/offline, features/tasks
docs/                             # contrato de sincronização e diagramas
```

## Decisões e premissas

Resumo das premissas e decisões de implementação.

- **Urgente:** palavra inteira `urgente` no título ou na descrição, sem diferenciar caixa; `urgentemente` e `urgentes` não contam; a regra é literal (`não urgente` conta). Calculada no cliente, funciona offline.
- **Ordenação:** urgentes primeiro; vencimento crescente (sem vencimento por último); criação crescente; id. Filtros são aplicados antes.
- **Agrupamento:** com "Agrupar por categoria" (padrão), a seção **Urgentes** fica no topo com as urgentes de todas as categorias e as demais aparecem agrupadas por categoria, sem duplicar.
- **`dueDate`** é data civil (`YYYY-MM-DD`), nunca convertida para UTC.
- **IndexedDB é a fonte da UI**, inclusive online. Cada escrita grava tarefa e mutação pendente na mesma transação.
- **Logout** remove só a sessão; dados e pendências locais do usuário são preservados para o próximo login dele.
- **Escrita no servidor** acontecerá apenas pelo `POST /api/sync/push` (Fase B), um único caminho com idempotência, change log e outbox.
- **Sem microsserviços nem broker:** monólito modular com um projeto de API e um de testes.

## Riscos e limitações atuais

- Sem Service Worker: recarregar a página **sem rede** ainda não funciona.
- Sem sincronização: alterações ficam apenas no dispositivo em que foram feitas.
- IndexedDB e `localStorage` (sessão) não são criptografados; dependem da proteção do dispositivo/perfil.
- Sem refresh token: após 8h é preciso novo login online para sincronizar (a edição local continua).
- Rate limit de login em memória, por IP: adequado para instância única.
- Migrations no startup são conveniência de dev/demo; em produção, aplicar via pipeline.

## Próximo passo (Fase B)

1. Migration com `processed_operations`, `user_sync_counters` e `task_change_log`.
2. `POST /api/sync/push` reaproveitando o `TaskCommandService` numa transação por operação (dedupe por `operationId`, `SELECT ... FOR UPDATE` no contador do usuário, change log), com testes de `duplicate`, `conflict` e isolamento.
3. `GET /api/sync/pull` paginado por cursor monotônico por usuário.
4. `SyncEngine` no Angular, PWA e UI de conflito.

## Apoio do Claude Code

A Fase A foi implementada com apoio do Claude Code (Anthropic): revisão do planejamento e resolução de conflitos entre os documentos, implementação do backend, do frontend, dos testes e desta documentação. Cada fatia foi verificada antes do commit com build e testes automatizados; a API foi testada com `curl` no container e o frontend com um teste manual no navegador (login válido e inválido, criação, urgente no topo, edição, conclusão, exclusão, filtros, agrupamento, tarefa vencida, recarga, logout/login e isolamento entre os dois usuários), conferindo console e requisições. O README final detalhará o apoio em todas as fases.

## Documentação

- [`docs/SYNC_CONTRACT.md`](docs/SYNC_CONTRACT.md): contratos HTTP, sincronização, idempotência e conflitos.
- [`docs/diagrams/`](docs/diagrams/): diagramas Mermaid.
