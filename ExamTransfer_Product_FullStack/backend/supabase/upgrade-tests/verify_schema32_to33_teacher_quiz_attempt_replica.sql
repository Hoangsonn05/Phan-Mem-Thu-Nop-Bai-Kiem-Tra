begin;
create extension if not exists pgtap with schema extensions;
set local search_path = public, extensions;
select plan(12);

select is((select schema_version from public.examtransfer_cloud_meta where id=1),33,
  'schema 32 upgrade reaches schema 33');
select is((select max(version) from supabase_migrations.schema_migrations),'20260807133059',
  'teacher quiz attempt replica migration is the latest applied migration');
select is((select name from public.organizations
  where id='c2100000-0000-0000-0000-000000000001'),'Schema 32 Replica Org',
  'organization survives upgrade');
select ok((select status='Finished' and access_mode='PublicCloud'
  from public.exam_sessions where id='c2400000-0000-0000-0000-000000000001'),
  'session survives upgrade');
select ok((select status='Approved' and source_mode='PublicCloud'
  from public.session_participants where id='c2500000-0000-0000-0000-000000000001'),
  'participant survives upgrade');
select ok((select status='Finalized' and grading_status='Graded'
    and auto_score=10 and score=10 and max_score=10 and returned_at is null
    and general_comment='Preserve comment' and jsonb_array_length(snapshot_json)=1
  from public.quiz_attempts where id='c2800000-0000-0000-0000-000000000001'),
  'attempt score, status, grading state and snapshot survive upgrade');
select ok((select revision=7 and choice_ids=
    '["c2700000-0000-4000-8000-000000000001"]'::jsonb
  from public.quiz_answers where id='c2900000-0000-0000-0000-000000000001'),
  'quiz answer and revision survive upgrade');
select ok('search_path=""' = any(coalesce((select proconfig from pg_proc where oid=
  'public.pull_teacher_quiz_attempts(uuid,bigint,timestamptz,uuid,integer)'::regprocedure),
  array[]::text[]))
  and has_function_privilege('authenticated',
    'public.pull_teacher_quiz_attempts(uuid,bigint,timestamptz,uuid,integer)','EXECUTE')
  and not has_function_privilege('anon',
    'public.pull_teacher_quiz_attempts(uuid,bigint,timestamptz,uuid,integer)','EXECUTE'),
  'pull RPC retains empty search path and guarded grants');

set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"c2000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select ok((select status='Finalized' and score=10 and max_score=10
    and finalized_at='2026-08-07 02:20+00'::timestamptz and cloud_version > 0
  from public.pull_teacher_quiz_attempts(
    'c2100000-0000-0000-0000-000000000001',0,null,null,500)
  where id='c2800000-0000-0000-0000-000000000001'),
  'same-organization teacher pulls the preserved authoritative attempt');
select ok(not ((select to_jsonb(attempt)::text
  from public.pull_teacher_quiz_attempts(
    'c2100000-0000-0000-0000-000000000001',0,null,null,500) attempt
  where id='c2800000-0000-0000-0000-000000000001')
  ~* 'is_correct|correctChoice|answerKey'),
  'upgraded teacher projection exposes no answer key');
select set_config('request.jwt.claims',
  '{"sub":"c2000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
select throws_ok($$select * from public.pull_teacher_quiz_attempts(
  'c2100000-0000-0000-0000-000000000001',0,null,null,500)$$,
  '42501','TEACHER_ROLE_REQUIRED',
  'student cannot use the upgraded teacher pull RPC');
reset role;
select ok(not has_table_privilege('authenticated','public.quiz_attempts','SELECT')
  and not has_column_privilege('authenticated','public.quiz_attempts','score','SELECT'),
  'upgrade does not broaden direct authenticated table score access');

select * from finish();
rollback;
