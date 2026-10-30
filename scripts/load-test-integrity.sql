-- Conferência de integridade depois do teste de carga (scripts/load-test.mjs).
-- Uso: docker compose exec -T db psql -U way2 -d way2_todo -tA -v prefix=load < scripts/load-test-integrity.sql
-- Considera os usuários de teste com e-mail '<prefix>-N@load.test'. Esperado, com a outbox já drenada:
-- operacoes_processadas = revisoes_change_log = eventos_outbox = outbox_entregues = eventos_por_revisao_distintos
-- (= operações aplicadas informadas pelo script), outbox_pendentes = 0 e usuarios_com_buraco_no_cursor = 0.
with u as (select id from users where email like :'prefix' || '-%@load.test'),
per_user as (
  select u.id,
         (select count(*) from task_change_log c where c.user_id = u.id) as revisions,
         (select coalesce(max(revision), 0) from task_change_log c where c.user_id = u.id) as max_revision,
         (select last_revision from user_sync_counters s where s.user_id = u.id) as counter
  from u)
select json_build_object(
  'usuarios', (select count(*) from u),
  'tarefas', (select count(*) from tasks t join u on u.id = t.user_id),
  'operacoes_processadas', (select count(*) from processed_operations p join u on u.id = p.user_id),
  'revisoes_change_log', (select count(*) from task_change_log c join u on u.id = c.user_id),
  'eventos_outbox', (select count(*) from integration_outbox o join u on u.id = o.user_id),
  'outbox_entregues', (select count(*) from integration_outbox o join u on u.id = o.user_id where o.status = 'delivered'),
  'outbox_pendentes', (select count(*) from integration_outbox o join u on u.id = o.user_id where o.status = 'pending'),
  'outbox_max_tentativas', (select coalesce(max(attempts), 0) from integration_outbox o join u on u.id = o.user_id),
  'usuarios_com_buraco_no_cursor', (select count(*) from per_user where revisions <> max_revision or coalesce(counter, 0) <> max_revision),
  'eventos_por_revisao_distintos', (select count(distinct (o.user_id, o.server_revision)) from integration_outbox o join u on u.id = o.user_id),
  'atraso_entrega_p95_s', (select round(percentile_cont(0.95) within group (order by extract(epoch from o.delivered_at - o.occurred_at))::numeric, 2)
                           from integration_outbox o join u on u.id = o.user_id where o.status = 'delivered')
);
