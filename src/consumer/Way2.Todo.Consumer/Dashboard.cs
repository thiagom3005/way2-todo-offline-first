namespace Way2.Todo.Consumer;

/// <summary>Página simples (atualização automática) para acompanhar os eventos recebidos na demonstração.</summary>
public static class Dashboard
{
    public const string Html = """
        <!doctype html>
        <html lang="pt-BR">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>Consumidor de webhooks</title>
          <style>
            body { font-family: system-ui, sans-serif; margin: 16px; color: #1d2330; background: #f5f6f8; }
            h1 { font-size: 1.3rem; } h2 { font-size: 1.05rem; margin-top: 24px; }
            .stats { display: flex; gap: 12px; flex-wrap: wrap; }
            .stat { background: #fff; border: 1px solid #d9dde5; border-radius: 8px; padding: 8px 12px; }
            table { border-collapse: collapse; width: 100%; background: #fff; font-size: 0.85rem; }
            th, td { border-bottom: 1px solid #d9dde5; padding: 6px 8px; text-align: left; }
            .dup { color: #c2410c; font-weight: 600; } .deleted { text-decoration: line-through; color: #5d6576; }
            .wrap { overflow-x: auto; } code { font-size: 0.8rem; }
          </style>
        </head>
        <body>
          <h1>Consumidor de webhooks (simulado)</h1>
          <p>Somente leitura. Deduplica por <code>eventId</code> e aplica só versões mais novas de cada tarefa.</p>
          <div class="stats" id="stats"></div>
          <p id="failure"></p>
          <h2>Tarefas (projeção local)</h2>
          <div class="wrap"><table id="tasks"></table></div>
          <h2>Eventos recebidos</h2>
          <div class="wrap"><table id="events"></table></div>
          <script>
            const esc = (v) => String(v ?? '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
            async function refresh() {
              const [events, tasks, failure] = await Promise.all(
                ['/events', '/tasks', '/failure-mode'].map((u) => fetch(u).then((r) => r.status === 204 ? null : r.json())));
              document.getElementById('stats').innerHTML =
                `<div class="stat">Eventos únicos: <b>${events.uniqueEvents}</b></div>` +
                `<div class="stat">Entregas recebidas: <b>${events.totalDeliveries}</b></div>` +
                `<div class="stat">Entregas duplicadas: <b>${events.duplicateDeliveries}</b></div>`;
              document.getElementById('failure').textContent = failure
                ? `Falha simulada ativa: ${JSON.stringify(failure)}` : 'Sem falha simulada.';
              document.getElementById('tasks').innerHTML = '<tr><th>Tarefa</th><th>Versão</th><th>Status</th><th>Categoria</th><th>Último evento</th></tr>' +
                tasks.map((t) => `<tr class="${t.deleted ? 'deleted' : ''}"><td>${esc(t.title)}</td><td>v${t.version}</td><td>${t.deleted ? 'excluída' : esc(t.status)}</td><td>${esc(t.category)}</td><td><code>${esc(t.lastEventId)}</code></td></tr>`).join('');
              document.getElementById('events').innerHTML = '<tr><th>Recebido</th><th>Tipo</th><th>Tarefa</th><th>Versão</th><th>Revisão</th><th>Entregas</th><th>eventId</th></tr>' +
                events.events.map((e) => `<tr><td>${new Date(e.receivedAt).toLocaleTimeString('pt-BR')}</td><td>${esc(e.eventType)}</td><td><code>${esc(e.taskId).slice(0, 8)}</code></td><td>v${e.taskVersion}</td><td>${e.serverRevision}</td><td class="${e.deliveries > 1 ? 'dup' : ''}">${e.deliveries}</td><td><code>${esc(e.eventId)}</code></td></tr>`).join('');
            }
            refresh();
            setInterval(refresh, 2000);
          </script>
        </body>
        </html>
        """;
}
