begin;
create extension if not exists pgtap with schema extensions;
set local search_path = public, extensions;
select plan(18);

select is((select schema_version from public.examtransfer_cloud_meta where id=1),32,
  'schema 31 upgrade reaches schema 32');
select is((select max(version) from supabase_migrations.schema_migrations),'20260807054739',
  'PC3 migration is the latest applied migration');
select is((select name from public.organizations
  where id='b3100000-0000-0000-0000-000000000001'),'Schema 31 Score History Org',
  'organization survives upgrade');
select ok((select role='Student' and student_code='SCHEMA31' and is_active
  from public.profiles where id='b3000000-0000-0000-0000-000000000002'),
  'student profile survives upgrade');
select results_eq($$select quiz_result_policy from public.exams
  where id in ('b3300000-0000-0000-0000-000000000001','b3300000-0000-0000-0000-000000000002')
  order by quiz_result_policy$$,
  $$values ('Hidden'),('ShowAfterSubmission')$$,
  'exam result policies survive upgrade');
select results_eq($$select room_code from public.exam_sessions
  where id in ('b3400000-0000-0000-0000-000000000001','b3400000-0000-0000-0000-000000000002')
  order by room_code$$,
  $$values ('S31HIDE'),('S31SHOW')$$,
  'session room codes survive upgrade');
select ok((select status='Finalized' and grading_status='Graded' and returned_at is null
    and auto_score=7.50 and score=7.50 and max_score=10
  from public.quiz_attempts where id='b3800000-0000-0000-0000-000000000001'),
  'legacy ShowAfterSubmission grading state and score are unchanged');
select ok((select status='Finalized' and grading_status='Graded' and returned_at is null
    and auto_score=10 and score=10 and max_score=10
  from public.quiz_attempts where id='b3800000-0000-0000-0000-000000000002'),
  'Hidden grading state and score are unchanged');
select is((select jsonb_array_length(snapshot_json) from public.quiz_attempts
  where id='b3800000-0000-0000-0000-000000000001'),2,
  'attempt snapshot survives upgrade');
select results_eq($$select revision from public.quiz_answers
  where attempt_id in ('b3800000-0000-0000-0000-000000000001','b3800000-0000-0000-0000-000000000002')
  order by revision$$,
  $$values (3::bigint),(4::bigint),(5::bigint)$$,
  'answer rows and revisions survive upgrade');

set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"b3000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
select ok((select result->>'scoreVisible'='true' and (result->>'score')::numeric=7.50
  from (select public.get_public_quiz_attempt(
    'b3800000-0000-0000-0000-000000000001') result) attempt),
  'legacy ShowAfterSubmission attempt becomes visible after migration');
select ok((select result->>'scoreVisible'='true'
    and result->>'correctAnswersVisible'='false'
    and (result->>'score')::numeric=7.50
  from (select public.get_public_quiz_attempt_review(
    'b3800000-0000-0000-0000-000000000001') result) review),
  'legacy review shows score without correct answers');
select is(pg_catalog.jsonb_array_length(public.get_student_results()->'items'),1,
  'history publishes only the legacy ShowAfterSubmission result');
select ok((select item->>'attemptId'='b3800000-0000-0000-0000-000000000001'
    and item->>'returnedAtUtc'=item->>'finalizedAtUtc'
    and item->>'startedAtUtc'='2026-08-07T00:10:00+00:00'
    and (item->>'durationSeconds')::bigint=754
  from pg_catalog.jsonb_array_elements(public.get_student_results()->'items') item),
  'upgraded history uses finalized publication time and server duration');
select ok((select result->>'scoreVisible'='false' and result->'score'='null'::jsonb
  from (select public.get_public_quiz_attempt(
    'b3800000-0000-0000-0000-000000000002') result) attempt),
  'Hidden attempt remains masked after migration');
select is(public.get_public_quiz_attempt_review(
  'b3800000-0000-0000-0000-000000000002')->>'scoreVisible','false',
  'Hidden review remains masked after migration');
select ok(not (public.get_student_results()->'items' @>
  '[{"attemptId":"b3800000-0000-0000-0000-000000000002"}]'::jsonb),
  'Hidden attempt remains absent from history');
select ok('search_path=""' = any(coalesce((select proconfig from pg_proc where oid=
  'public.get_student_results(integer,timestamptz,text,uuid)'::regprocedure),array[]::text[]))
  and has_function_privilege('authenticated',
    'public.get_student_results(integer,timestamptz,text,uuid)','EXECUTE')
  and not has_function_privilege('anon',
    'public.get_student_results(integer,timestamptz,text,uuid)','EXECUTE'),
  'student results RPC retains empty search_path and guarded grants');

select * from finish();
rollback;
