begin;
select plan(12);

select is((select schema_version from public.examtransfer_cloud_meta where id=1),23,
  'schema 22 upgrade reaches schema 23');
select is(
  (select grading_status from public.quiz_attempts
   where id='52600000-0000-0000-0000-000000000000'),
  'Returned',
  'existing returned grading status survives upgrade');
select is(
  (select score from public.quiz_attempts
   where id='52600000-0000-0000-0000-000000000000'),
  8.00::numeric,
  'existing teacher score survives upgrade');
select is(
  (select general_comment from public.quiz_attempts
   where id='52600000-0000-0000-0000-000000000000'),
  'Schema 22 preserved comment',
  'existing teacher comment survives upgrade');
select ok(
  (select returned_at is not null from public.quiz_attempts
   where id='52600000-0000-0000-0000-000000000000'),
  'existing return visibility survives upgrade');
select has_function('public','save_public_quiz_grade',
  array['uuid','numeric','text','bigint','uuid'],
  'save grade RPC exists after upgrade');
select has_function('public','return_public_quiz_grade',
  array['uuid','text','bigint','uuid'],
  'return grade RPC exists after upgrade');
select has_function('public','reopen_public_quiz_grade',
  array['uuid','text','bigint','uuid'],
  'reopen grade RPC exists after upgrade');
select has_trigger(
  'public','quiz_attempts','quiz_attempts_notify_grade_returned',
  'device-target grade visibility trigger exists after upgrade');
select ok(position(
  '''exam-session:'' || new.session_id::text || '':device:'''
  in lower(pg_get_functiondef(
    'private.notify_public_quiz_grade_returned()'::regprocedure))) > 0,
  'upgraded trigger targets device topics');
select ok(position(
  '''score'''
  in lower(pg_get_functiondef(
    'private.notify_public_quiz_grade_returned()'::regprocedure))) = 0,
  'upgraded trigger does not broadcast score');
select ok(position(
  '''reopen_public_quiz_grade'''
  in lower(pg_get_functiondef(
    'public.get_examtransfer_cloud_capabilities()'::regprocedure))) > 0,
  'schema 23 capability contract includes grading mutations');

select * from finish();
rollback;
