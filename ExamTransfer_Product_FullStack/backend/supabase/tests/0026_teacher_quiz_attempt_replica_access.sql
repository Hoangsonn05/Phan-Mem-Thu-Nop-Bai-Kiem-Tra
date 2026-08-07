begin;
create extension if not exists pgtap with schema extensions;
set local search_path = public, extensions;
select plan(9);

insert into auth.users(id,email) values
  ('b2000000-0000-0000-0000-000000000001','replica-teacher@example.test'),
  ('b2000000-0000-0000-0000-000000000002','replica-other-teacher@example.test'),
  ('b2000000-0000-0000-0000-000000000003','replica-owner@example.test'),
  ('b2000000-0000-0000-0000-000000000004','replica-peer@example.test')
on conflict (id) do nothing;

insert into public.organizations(id,name) values
  ('b2100000-0000-0000-0000-000000000001','Replica Org'),
  ('b2100000-0000-0000-0000-000000000002','Replica Other Org');

insert into public.profiles(
  id,organization_id,display_name,role,username,student_code,is_active,date_of_birth)
values
  ('b2000000-0000-0000-0000-000000000001','b2100000-0000-0000-0000-000000000001',
   'Replica Teacher','Teacher','replica-teacher',null,true,null),
  ('b2000000-0000-0000-0000-000000000002','b2100000-0000-0000-0000-000000000002',
   'Replica Other Teacher','Teacher','replica-other-teacher',null,true,null),
  ('b2000000-0000-0000-0000-000000000003','b2100000-0000-0000-0000-000000000001',
   'Replica Owner','Student','REPLICA-OWNER','REPLICA-OWNER',true,'2008-01-01'),
  ('b2000000-0000-0000-0000-000000000004','b2100000-0000-0000-0000-000000000001',
   'Replica Peer','Student','REPLICA-PEER','REPLICA-PEER',true,'2008-01-01');

insert into public.classes(
  id,organization_id,name,code,school_year,status,access_mode,created_by,created_at,updated_at)
values (
  'b2200000-0000-0000-0000-000000000001','b2100000-0000-0000-0000-000000000001',
  'Replica Class','REPLICA','2026','Active','Public',
  'b2000000-0000-0000-0000-000000000001',now(),now());

insert into public.exams(
  id,organization_id,class_id,title,subject,duration_minutes,status,version,created_by,
  delivery_type,quiz_result_policy,supervision_mode,created_at,updated_at)
values (
  'b2300000-0000-0000-0000-000000000001','b2100000-0000-0000-0000-000000000001',
  'b2200000-0000-0000-0000-000000000001','Replica Quiz','Test',60,'Published',1,
  'b2000000-0000-0000-0000-000000000001','MultipleChoice','Hidden','Standard',now(),now());

insert into public.exam_sessions(
  id,organization_id,exam_id,class_id,room_code,status,started_at,access_mode,admission_mode,
  auto_approve,accepting_participants,delivery_type,supervision_mode,quiz_result_policy,
  exam_version,created_at,updated_at)
values (
  'b2400000-0000-0000-0000-000000000001','b2100000-0000-0000-0000-000000000001',
  'b2300000-0000-0000-0000-000000000001','b2200000-0000-0000-0000-000000000001',
  'RPL001','Finished','2026-08-07 01:00+00','PublicCloud','ClassMembersOnly',false,false,
  'MultipleChoice','Standard','Hidden',1,now(),now());

insert into public.session_participants(
  id,organization_id,session_id,user_id,student_code,display_name,device_id,status,
  joined_at,download_status,submission_status,extra_time_minutes,resubmit_allowed,
  source_mode,created_at,updated_at)
values
  ('b2500000-0000-0000-0000-000000000001','b2100000-0000-0000-0000-000000000001',
   'b2400000-0000-0000-0000-000000000001','b2000000-0000-0000-0000-000000000003',
   'REPLICA-OWNER','Replica Owner','replica-owner-device','Approved',now(),
   'Completed','Submitted',0,false,'PublicCloud',now(),now()),
  ('b2500000-0000-0000-0000-000000000002','b2100000-0000-0000-0000-000000000001',
   'b2400000-0000-0000-0000-000000000001','b2000000-0000-0000-0000-000000000004',
   'REPLICA-PEER','Replica Peer','replica-peer-device','Approved',now(),
   'Completed','NotSubmitted',0,false,'PublicCloud',now(),now());

insert into public.quiz_attempts(
  id,organization_id,session_id,participant_id,attempt_number,exam_version,result_policy,status,
  started_at,deadline_at,finalized_at,auto_score,score,max_score,grading_status,graded_at,
  returned_at,snapshot_json,source_mode,created_at,updated_at)
values (
  'b2600000-0000-0000-0000-000000000001','b2100000-0000-0000-0000-000000000001',
  'b2400000-0000-0000-0000-000000000001','b2500000-0000-0000-0000-000000000001',1,1,
  'Hidden','Finalized','2026-08-07 01:05+00','2026-08-07 02:05+00','2026-08-07 01:35+00',
  8.00,8.00,10.00,'Graded','2026-08-07 01:35+00',null,
  '[{"id":"b2700000-0000-0000-0000-000000000001","points":10,"choices":[{"id":"b2800000-0000-0000-0000-000000000001"}]}]'::jsonb,
  'PublicCloud',now(),now());

select has_function('public','pull_teacher_quiz_attempts',
  array['uuid','bigint','timestamp with time zone','uuid','integer'],
  'dedicated teacher quiz attempt pull RPC exists');
select ok(
  has_function_privilege('authenticated',
    'public.pull_teacher_quiz_attempts(uuid,bigint,timestamptz,uuid,integer)','EXECUTE')
  and not has_function_privilege('anon',
    'public.pull_teacher_quiz_attempts(uuid,bigint,timestamptz,uuid,integer)','EXECUTE'),
  'only authenticated callers may invoke the guarded pull RPC');

set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"b2000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select lives_ok($$
  select * from public.pull_teacher_quiz_attempts(
    'b2100000-0000-0000-0000-000000000001',0,null,null,500)$$,
  'same-organization teacher can read the complete quiz attempt pull projection');
select ok((select status='Finalized'
    and source_mode='PublicCloud'
    and score=8.00
    and max_score=10.00
    and finalized_at='2026-08-07 01:35+00'::timestamptz
    and cloud_version > 0
  from public.pull_teacher_quiz_attempts(
    'b2100000-0000-0000-0000-000000000001',0,null,null,500)
  where id='b2600000-0000-0000-0000-000000000001'),
  'teacher pull returns the authoritative score, timing, source and cloud version');

select set_config('request.jwt.claims',
  '{"sub":"b2000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
select throws_ok($$select * from public.pull_teacher_quiz_attempts(
  'b2100000-0000-0000-0000-000000000001',0,null,null,500)$$,
  '42501','PUBLIC_ORGANIZATION_FORBIDDEN',
  'different-organization teacher cannot read the attempt');

select set_config('request.jwt.claims',
  '{"sub":"b2000000-0000-0000-0000-000000000003","role":"authenticated"}',true);
select throws_ok($$select * from public.pull_teacher_quiz_attempts(
  'b2100000-0000-0000-0000-000000000001',0,null,null,500)$$,
  '42501','TEACHER_ROLE_REQUIRED',
  'student cannot use the teacher pull RPC for an unreturned Hidden attempt');

select set_config('request.jwt.claims',
  '{"sub":"b2000000-0000-0000-0000-000000000004","role":"authenticated"}',true);
select throws_ok($$select * from public.pull_teacher_quiz_attempts(
  'b2100000-0000-0000-0000-000000000001',0,null,null,500)$$,
  '42501','TEACHER_ROLE_REQUIRED',
  'student cannot use the teacher pull RPC for another student attempt');
reset role;

select ok(not has_table_privilege('authenticated','public.quiz_attempts','SELECT')
  and not has_column_privilege('authenticated','public.quiz_attempts','score','SELECT')
  and not has_table_privilege('anon','public.quiz_attempts','SELECT'),
  'teacher replica fix does not broaden direct table score access');

set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"b2000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select ok(not ((select to_jsonb(attempt)::text
  from public.pull_teacher_quiz_attempts(
    'b2100000-0000-0000-0000-000000000001',0,null,null,500) attempt
  where id='b2600000-0000-0000-0000-000000000001')
  ~* 'is_correct|correctChoice|answerKey'),
  'teacher replica projection exposes no answer key');

select * from finish();
rollback;
