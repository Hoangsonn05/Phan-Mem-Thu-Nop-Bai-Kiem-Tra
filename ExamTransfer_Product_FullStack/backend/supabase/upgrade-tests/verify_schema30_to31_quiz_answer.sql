begin;
create extension if not exists pgtap with schema extensions;
set local search_path = public, extensions;
select plan(9);

select is((select schema_version from public.examtransfer_cloud_meta where id=1),31,
  'schema 30 upgrade reaches schema 31');
select is((select choice_ids from public.quiz_answers
  where id='93900000-0000-0000-0000-000000000001'),'[]'::jsonb,
  'existing empty answer survives upgrade');
select is((select revision from public.quiz_answers
  where id='93900000-0000-0000-0000-000000000001'),7::bigint,
  'existing answer revision survives upgrade');
select is((select jsonb_array_length(snapshot_json) from public.quiz_attempts
  where id='93800000-0000-0000-0000-000000000001'),1,
  'existing attempt snapshot survives upgrade');
select has_function('private','is_public_quiz_attempt_snapshot_payload_valid',array['jsonb'],
  'snapshot payload validator exists after upgrade');
select ok((select prosecdef from pg_proc where oid=
  'public.save_public_quiz_answers(uuid,uuid,jsonb,bigint,timestamptz)'::regprocedure),
  'save answer RPC retains SECURITY DEFINER');
select ok('search_path=""' = any(coalesce((select proconfig from pg_proc where oid=
  'public.save_public_quiz_answers(uuid,uuid,jsonb,bigint,timestamptz)'::regprocedure),array[]::text[])),
  'save answer RPC retains empty search_path');
select ok(has_function_privilege('authenticated',
  'public.save_public_quiz_answers(uuid,uuid,jsonb,bigint,timestamptz)','EXECUTE'),
  'authenticated retains save answer EXECUTE');
select ok(position('snapshot_json' in lower(pg_get_functiondef(
  'public.save_public_quiz_answers(uuid,uuid,jsonb,bigint,timestamptz)'::regprocedure))) > 0,
  'save answer RPC uses the persisted attempt snapshot');

select * from finish();
rollback;
