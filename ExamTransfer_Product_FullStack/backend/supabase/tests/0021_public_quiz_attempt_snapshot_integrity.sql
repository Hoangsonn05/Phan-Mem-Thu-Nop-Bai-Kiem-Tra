begin;
create extension if not exists pgtap with schema extensions;
set local search_path = public, extensions;
select plan(26);

select has_function('private','is_public_quiz_attempt_snapshot_valid',array['jsonb','uuid','uuid','integer'],
  'private snapshot validator exists');
select has_function('public','start_public_quiz_attempt',array['uuid','text'],
  'start RPC signature is preserved');
select has_function('public','get_public_quiz_attempt',array['uuid'],
  'get RPC signature is preserved');
select ok((select prosecdef from pg_proc where oid=
  'public.start_public_quiz_attempt(uuid,text)'::regprocedure),
  'start RPC retains SECURITY DEFINER');
select ok((select prosecdef from pg_proc where oid=
  'public.get_public_quiz_attempt(uuid)'::regprocedure),
  'get RPC retains SECURITY DEFINER');
select ok('search_path=""' = any(coalesce((select proconfig from pg_proc where oid=
  'public.start_public_quiz_attempt(uuid,text)'::regprocedure),array[]::text[])),
  'start RPC retains empty search_path');
select ok('search_path=""' = any(coalesce((select proconfig from pg_proc where oid=
  'public.get_public_quiz_attempt(uuid)'::regprocedure),array[]::text[])),
  'get RPC retains empty search_path');
select ok(has_function_privilege('authenticated',
  'public.start_public_quiz_attempt(uuid,text)','EXECUTE'),
  'authenticated retains start EXECUTE');
select ok(has_function_privilege('authenticated',
  'public.get_public_quiz_attempt(uuid)','EXECUTE'),
  'authenticated retains get EXECUTE');
select ok(not has_function_privilege('anon',
  'public.start_public_quiz_attempt(uuid,text)','EXECUTE'),
  'anon cannot start quiz attempts');
select ok(not has_function_privilege('anon',
  'public.get_public_quiz_attempt(uuid)','EXECUTE'),
  'anon cannot read quiz attempts');
select ok(position('for update' in lower(pg_get_functiondef(
  'public.start_public_quiz_attempt(uuid,text)'::regprocedure))) > 0,
  'start RPC row-locks an existing attempt');
select ok(position('pg_advisory_xact_lock' in lower(pg_get_functiondef(
  'public.start_public_quiz_attempt(uuid,text)'::regprocedure))) > 0,
  'start RPC retains advisory race protection');

insert into auth.users(id,email) values
  ('81000000-0000-0000-0000-000000000001','slice-c-teacher@example.test'),
  ('81000000-0000-0000-0000-000000000002','slice-c-student@example.test'),
  ('81000000-0000-0000-0000-000000000003','slice-c-peer@example.test')
on conflict (id) do nothing;
insert into public.organizations(id,name) values
  ('81100000-0000-0000-0000-000000000001','Slice C Org');
insert into public.profiles(
  id,organization_id,display_name,role,username,student_code,is_active,date_of_birth)
values
  ('81000000-0000-0000-0000-000000000001','81100000-0000-0000-0000-000000000001',
   'Slice C Teacher','Teacher','slice-c-teacher',null,true,null),
  ('81000000-0000-0000-0000-000000000002','81100000-0000-0000-0000-000000000001',
   'Slice C Student','Student','SC-S1','SC-S1',true,'2008-01-01'),
  ('81000000-0000-0000-0000-000000000003','81100000-0000-0000-0000-000000000001',
   'Slice C Peer','Student','SC-S2','SC-S2',true,'2008-01-01');
insert into public.classes(
  id,organization_id,name,code,school_year,status,access_mode,created_by,created_at,updated_at)
values ('81200000-0000-0000-0000-000000000001','81100000-0000-0000-0000-000000000001',
  'Slice C Class','SCCLS','2026','Active','Public',
  '81000000-0000-0000-0000-000000000001',now(),now());
insert into public.class_members(
  id,organization_id,class_id,user_id,student_code,display_name,created_at,updated_at)
values
  ('81210000-0000-0000-0000-000000000001','81100000-0000-0000-0000-000000000001',
   '81200000-0000-0000-0000-000000000001','81000000-0000-0000-0000-000000000002',
   'SC-S1','Slice C Student',now(),now()),
  ('81210000-0000-0000-0000-000000000002','81100000-0000-0000-0000-000000000001',
   '81200000-0000-0000-0000-000000000001','81000000-0000-0000-0000-000000000003',
   'SC-S2','Slice C Peer',now(),now());
insert into public.exams(
  id,organization_id,class_id,title,subject,duration_minutes,status,version,created_by,
  delivery_type,quiz_result_policy,supervision_mode,created_at,updated_at)
values
  ('81300000-0000-0000-0000-000000000001','81100000-0000-0000-0000-000000000001',
   '81200000-0000-0000-0000-000000000001','Slice C Quiz','Test',30,'Published',1,
   '81000000-0000-0000-0000-000000000001','MultipleChoice','Hidden','Standard',now(),now()),
  ('81300000-0000-0000-0000-000000000002','81100000-0000-0000-0000-000000000001',
   '81200000-0000-0000-0000-000000000001','Slice C Empty Quiz','Test',30,'Published',1,
   '81000000-0000-0000-0000-000000000001','MultipleChoice','Hidden','Standard',now(),now());
insert into public.public_class_assignments(organization_id,class_id,exam_id) values
  ('81100000-0000-0000-0000-000000000001','81200000-0000-0000-0000-000000000001',
   '81300000-0000-0000-0000-000000000001'),
  ('81100000-0000-0000-0000-000000000001','81200000-0000-0000-0000-000000000001',
   '81300000-0000-0000-0000-000000000002');
insert into public.exam_sessions(
  id,organization_id,exam_id,class_id,room_code,status,started_at,access_mode,admission_mode,
  auto_approve,accepting_participants,delivery_type,supervision_mode,quiz_result_policy,
  exam_version,created_at,updated_at)
values
  ('81400000-0000-0000-0000-000000000001','81100000-0000-0000-0000-000000000001',
   '81300000-0000-0000-0000-000000000001','81200000-0000-0000-0000-000000000001',
   'SCQUIZ','Waiting',now(),'PublicCloud','ClassMembersOnly',true,true,
   'MultipleChoice','Standard','Hidden',1,now(),now()),
  ('81400000-0000-0000-0000-000000000002','81100000-0000-0000-0000-000000000001',
   '81300000-0000-0000-0000-000000000002','81200000-0000-0000-0000-000000000001',
   'SCEMPTY','Waiting',now(),'PublicCloud','ClassMembersOnly',true,true,
   'MultipleChoice','Standard','Hidden',1,now(),now());
insert into public.quiz_questions(
  id,organization_id,exam_id,version,sort_order,question_text,points,multiple)
values
  ('81500000-0000-0000-0000-000000000001','81100000-0000-0000-0000-000000000001',
   '81300000-0000-0000-0000-000000000001',1,1,'Slice C question',10,false);
insert into public.quiz_choices(
  id,organization_id,question_id,sort_order,choice_text,is_correct)
values
  ('81600000-0000-4000-8000-000000000001','81100000-0000-0000-0000-000000000001',
   '81500000-0000-0000-0000-000000000001',1,'Option A',true),
  ('81600000-0000-4000-8000-000000000002','81100000-0000-0000-0000-000000000001',
   '81500000-0000-0000-0000-000000000001',2,'Option B',false);

set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"81000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
create temporary table slice_c_values(key text primary key,value uuid) on commit drop;
insert into slice_c_values values ('participant',public.join_public_session(
  '81400000-0000-0000-0000-000000000001','slice-c-device','test','1','{}'));
insert into slice_c_values values ('connection',public.upsert_public_device_heartbeat(
  '81400000-0000-0000-0000-000000000001','slice-c-device','Online','ExamTransfer','[]','1','1'));
insert into slice_c_values values ('empty_participant',public.join_public_session(
  '81400000-0000-0000-0000-000000000002','slice-c-empty-device','test','1','{}'));
insert into slice_c_values values ('empty_connection',public.upsert_public_device_heartbeat(
  '81400000-0000-0000-0000-000000000002','slice-c-empty-device','Online','ExamTransfer','[]','1','1'));
reset role;
update public.public_device_connections
set policy_state='Applied',policy_lease_expires_at=now()+interval '2 hours'
where id in ((select value from slice_c_values where key='connection'),
             (select value from slice_c_values where key='empty_connection'));
update public.exam_sessions set status='InProgress'
where id in ('81400000-0000-0000-0000-000000000001',
             '81400000-0000-0000-0000-000000000002');

set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"81000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
select throws_ok($$select public.start_public_quiz_attempt(
  '81400000-0000-0000-0000-000000000002','slice-c-empty-start')$$,
  '55000','QUIZ_HAS_NO_QUESTIONS','zero-question exam is rejected');
insert into slice_c_values values ('attempt',public.start_public_quiz_attempt(
  '81400000-0000-0000-0000-000000000001','slice-c-valid-start'));
select is(public.start_public_quiz_attempt(
  '81400000-0000-0000-0000-000000000001','slice-c-valid-start'),
  (select value from slice_c_values where key='attempt'),
  'repeated start returns the same attempt id');
select is(jsonb_array_length(public.get_public_quiz_attempt(
  (select value from slice_c_values where key='attempt'))->'questions'),1,
  'new attempt returns one validated question');
select ok(position('isCorrect' in public.get_public_quiz_attempt(
  (select value from slice_c_values where key='attempt'))::text)=0,
  'student attempt does not expose answer keys');

reset role;
create temporary table slice_c_times as
select started_at,deadline_at from public.quiz_attempts
where id=(select value from slice_c_values where key='attempt');
update public.quiz_attempts set snapshot_json='[]'::jsonb
where id=(select value from slice_c_values where key='attempt');
set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"81000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
select is(public.start_public_quiz_attempt(
  '81400000-0000-0000-0000-000000000001','slice-c-repair-start'),
  (select value from slice_c_values where key='attempt'),
  'empty unanswered attempt repairs with the same id');
reset role;
select is((select jsonb_array_length(snapshot_json) from public.quiz_attempts
  where id=(select value from slice_c_values where key='attempt')),1,
  'repair restores a non-empty snapshot');
select ok((select a.started_at=t.started_at and a.deadline_at=t.deadline_at
  from public.quiz_attempts a cross join slice_c_times t
  where a.id=(select value from slice_c_values where key='attempt')),
  'repair preserves started_at and deadline_at');

update public.quiz_attempts set snapshot_json='[]'::jsonb
where id=(select value from slice_c_values where key='attempt');
insert into public.quiz_answers(
  id,organization_id,attempt_id,question_id,choice_ids,revision,client_updated_at,
  source_mode,created_at,updated_at)
values ('81700000-0000-0000-0000-000000000001','81100000-0000-0000-0000-000000000001',
  (select value from slice_c_values where key='attempt'),
  '81500000-0000-0000-0000-000000000001','[]'::jsonb,1,now(),'PublicCloud',now(),now());
set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"81000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
select throws_ok($$select public.start_public_quiz_attempt(
  '81400000-0000-0000-0000-000000000001','slice-c-answer-block')$$,
  '55000','QUIZ_ATTEMPT_SNAPSHOT_INVALID','answered empty attempt is not repaired');
select throws_ok($$select public.get_public_quiz_attempt(
  (select value from slice_c_values where key='attempt'))$$,
  '55000','QUIZ_ATTEMPT_SNAPSHOT_INVALID','get RPC rejects empty questions');
reset role;
select is((select snapshot_json from public.quiz_attempts
  where id=(select value from slice_c_values where key='attempt')),'[]'::jsonb,
  'blocked repair leaves snapshot unchanged');
select is((select count(*)::integer from public.quiz_answers
  where attempt_id=(select value from slice_c_values where key='attempt')),1,
  'blocked repair preserves saved answers');

delete from public.quiz_answers
where attempt_id=(select value from slice_c_values where key='attempt');
update public.quiz_attempts
set status='Finalized',finalized_at=now(),grading_status='Graded',auto_score=0,score=0
where id=(select value from slice_c_values where key='attempt');
set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"81000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
select throws_ok($$select public.start_public_quiz_attempt(
  '81400000-0000-0000-0000-000000000001','slice-c-terminal-block')$$,
  '55000','QUIZ_ATTEMPT_SNAPSHOT_INVALID','terminal empty attempt is not repaired');
select set_config('request.jwt.claims',
  '{"sub":"81000000-0000-0000-0000-000000000003","role":"authenticated"}',true);
select throws_ok($$select public.get_public_quiz_attempt(
  (select value from slice_c_values where key='attempt'))$$,
  'P0002','PUBLIC_QUIZ_ATTEMPT_NOT_FOUND','another student cannot read the attempt');

select * from finish();
rollback;
