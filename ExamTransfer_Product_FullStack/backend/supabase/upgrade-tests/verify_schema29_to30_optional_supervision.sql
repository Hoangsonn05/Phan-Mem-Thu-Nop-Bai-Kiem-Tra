begin;
create extension if not exists pgtap with schema extensions;
set local search_path = public, extensions;
select plan(8);

select is((select schema_version from public.examtransfer_cloud_meta where id=1),30,
  'schema 29 upgrade reaches schema 30');
select is((select count(*) from public.exams
  where id='92300000-0000-0000-0000-000000000001'),1::bigint,
  'existing quiz exam survives upgrade');
select is((select count(*) from public.quiz_questions
  where exam_id='92300000-0000-0000-0000-000000000001'),1::bigint,
  'existing quiz question survives upgrade');
select is((select count(*) from public.quiz_choices
  where question_id='92400000-0000-0000-0000-000000000001'),2::bigint,
  'existing quiz choices survive upgrade');
select has_function('public','start_public_quiz_attempt',array['uuid','text'],
  'snapshot-integrity start RPC exists after upgrade');
select has_function('public','get_public_quiz_attempt',array['uuid'],
  'snapshot-integrity read RPC exists after upgrade');
select has_function('private','is_public_quiz_attempt_snapshot_valid',
  array['jsonb','uuid','uuid','integer'],
  'snapshot validator exists after upgrade');
select ok(position(
  'select schema_version from public.examtransfer_cloud_meta'
  in lower(pg_get_functiondef(
    'public.get_examtransfer_cloud_capabilities()'::regprocedure))) > 0,
  'capability RPC remains dynamic after upgrade');

select * from finish();
rollback;
