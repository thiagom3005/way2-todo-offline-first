# Way2 — Todo List Offline-First | Planejamento técnico

> **Fase atual:** arquitetura e contratos definidos. **Não contém aplicação implementada.**
>
> Referência: *Desafio técnico — Aplicação Todo List Offline-First*, Way2 Technology, 8 páginas.

## Documentos

- [`docs/SYNC_CONTRACT.md`](docs/SYNC_CONTRACT.md): contratos HTTP e regras completas de sincronização, idempotência e conflitos.
- [`docs/diagrams/`](docs/diagrams/): diagramas Mermaid (visão geral e sequências).

## Stack definida

- Backend: ASP.NET Core (.NET moderno), C#, EF Core e PostgreSQL.
- Frontend: Angular, Signals, PWA, IndexedDB com Dexie.js.
- Integrador externo: `BackgroundService` com Transactional Outbox e webhook HTTP para consumidor simulado.
- Execução: Docker Compose para banco/API/consumidor; frontend em modo PWA servido localmente.
- Testes: xUnit e testes do frontend nos fluxos críticos.

## O que demonstrar

1. Iniciar a aplicação online com usuário de teste previamente autenticado.
2. Desconectar e criar/editar/concluir/remover tarefas; recarregar e continuar operando com dados locais.
3. Criar uma tarefa contendo `URGENTE` e conferir que sobe ao topo imediatamente.
4. Reconectar; verificar sincronização automática e indicador `Sincronizando` → `Sincronizado`.
5. Verificar evento chegando no consumidor externo **somente após** aceitação do servidor.
6. Simular falha no webhook, observar retry sem perda de evento e recepção idempotente.
7. Em dois clientes, modificar a mesma tarefa a partir da mesma versão e demonstrar conflito resolvível.

## Premissas de avaliação

- A primeira autenticação exige conexão; sessões já autenticadas podem trabalhar com dados locais offline.
- Categorias são um campo `category` livre na tarefa, permitindo agrupamento offline sem outra entidade.
- “urgente” é palavra isolada, comparação independente de maiúsculas/minúsculas; `urgentemente` não conta.
- Ordenação: urgente primeiro; dentro dos grupos, vencimento crescente (sem vencimento por último), criação crescente e ID.
- Sem múltiplas instâncias de worker na prova. Ao evoluir, adotar reserva de eventos e garantias explícitas de ordenação por tarefa.
- Sincronização em segundo plano com app fechado **não é garantida**; ocorre automaticamente com o app aberto quando a rede volta, ao reabrir, retomar foco ou nos retries.

## Comandos de implementação (quando a aplicação existir)

O README final deve substituir este guia de planejamento por instruções reproduzíveis de execução, credenciais do usuário de teste, variáveis de ambiente e roteiro da demonstração.
