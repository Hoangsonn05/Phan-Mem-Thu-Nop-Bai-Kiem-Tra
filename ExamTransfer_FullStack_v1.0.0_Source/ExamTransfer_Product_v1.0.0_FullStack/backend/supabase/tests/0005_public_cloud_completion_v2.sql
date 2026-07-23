begin;
select plan(14);

select is((select schema_version from public.examtransfer_cloud_meta where id=1), 14,
  'cloud schema compatibility version is 14');
select has_function('public', 'verify_public_submission_archive',
  array['uuid','uuid','text','bigint','text'], 'transactional archive verification RPC exists');
select has_function('public', 'get_public_exam_file_download',
  array['uuid','uuid'], 'authorized exam download metadata RPC exists');
select has_function('public', 'get_examtransfer_cloud_capabilities',
  array[]::text[], 'capability RPC exists');
select ok(has_function_privilege('service_role',
  'public.verify_public_submission_archive(uuid,uuid,text,bigint,text)', 'EXECUTE'),
  'service role can verify archives');
select ok(not has_function_privilege('authenticated',
  'public.verify_public_submission_archive(uuid,uuid,text,bigint,text)', 'EXECUTE'),
  'authenticated users cannot verify archives');
select has_index('public', 'submission_files', 'ux_public_submission_single_file',
  'PublicCloud-only single archive index exists');
select like((select pg_get_expr(i.indpred, i.indrelid)
  from pg_index i join pg_class c on c.oid=i.indexrelid
  where c.relname='ux_public_submission_single_file'), '%PublicCloud%',
  'single archive index is partial for PublicCloud');
select like((select pg_get_expr(i.indpred, i.indrelid)
  from pg_index i join pg_class c on c.oid=i.indexrelid
  where c.relname='ux_public_submission_idempotency'), '%PublicCloud%',
  'idempotency index is partial for PublicCloud');
select has_column('public', 'cloud_sync_cursors', 'entity_name', 'cursor is per entity');
select has_column('public', 'cloud_sync_cursors', 'last_updated_at', 'cursor stores updated_at tie breaker');
select has_column('public', 'cloud_sync_cursors', 'last_id', 'cursor stores id tie breaker');
select like(pg_get_functiondef('public.enforce_student_submission_policy()'::regprocedure),
  '%source_mode <> ''PublicCloud''%', 'archive limit bypasses legacy LAN rows');
select is((select count(*) from pg_policies where schemaname='realtime'
  and tablename='messages' and policyname='examtransfer_broadcast_send'
  and with_check like '%:telemetry:%'), 1::bigint,
  'student broadcast is restricted to its telemetry topic');

select * from finish();
rollback;
