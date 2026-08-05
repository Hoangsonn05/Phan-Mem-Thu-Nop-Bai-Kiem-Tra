begin;
create extension if not exists pgtap with schema extensions;
set local search_path = public, extensions;
select plan(5);

select is(
  (select schema_version from public.examtransfer_cloud_meta where id = 1),
  29,
  'PublicCloud quiz runtime gate advances schema compatibility to 29');
select has_function('public','start_public_quiz_attempt',array['uuid','text'],
  'quiz attempt start RPC remains available');
select has_function('public','get_public_quiz_attempt',array['uuid'],
  'quiz attempt read RPC remains available');
select has_function('private','is_public_quiz_attempt_snapshot_valid',
  array['jsonb','uuid','uuid','integer'],
  'quiz attempt snapshot validator remains available');
select ok(position(
  'select schema_version from public.examtransfer_cloud_meta'
  in lower(pg_get_functiondef(
    'public.get_examtransfer_cloud_capabilities()'::regprocedure))) > 0,
  'capability RPC reads the current schema version dynamically');

select * from finish();
rollback;
