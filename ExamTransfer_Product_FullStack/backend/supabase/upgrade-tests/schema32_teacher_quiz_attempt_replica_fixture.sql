do $fixture_precondition$
begin
  if (select max(version) from supabase_migrations.schema_migrations)
       <> '20260807054739'
     or (select schema_version from public.examtransfer_cloud_meta where id=1)
       <> 32 then
    raise exception 'SCHEMA32_FIXTURE_WRONG_MIGRATION_TARGET';
  end if;
end
$fixture_precondition$;

insert into auth.users(id,email) values
  ('c2000000-0000-0000-0000-000000000001','schema32-teacher@example.test'),
  ('c2000000-0000-0000-0000-000000000002','schema32-student@example.test');
insert into public.organizations(id,name) values
  ('c2100000-0000-0000-0000-000000000001','Schema 32 Replica Org');
insert into public.profiles(
  id,organization_id,display_name,role,username,student_code,is_active,date_of_birth)
values
  ('c2000000-0000-0000-0000-000000000001','c2100000-0000-0000-0000-000000000001',
   'Schema 32 Teacher','Teacher','schema32-teacher',null,true,null),
  ('c2000000-0000-0000-0000-000000000002','c2100000-0000-0000-0000-000000000001',
   'Schema 32 Student','Student','SCHEMA32','SCHEMA32',true,'2008-01-01');
insert into public.classes(
  id,organization_id,name,code,school_year,status,access_mode,created_by,created_at,updated_at)
values (
  'c2200000-0000-0000-0000-000000000001','c2100000-0000-0000-0000-000000000001',
  'Schema 32 Class','S32','2026','Active','Public',
  'c2000000-0000-0000-0000-000000000001',now(),now());
insert into public.exams(
  id,organization_id,class_id,title,subject,duration_minutes,status,version,created_by,
  delivery_type,quiz_result_policy,supervision_mode,created_at,updated_at)
values (
  'c2300000-0000-0000-0000-000000000001','c2100000-0000-0000-0000-000000000001',
  'c2200000-0000-0000-0000-000000000001','Schema 32 Quiz','Test',30,'Published',1,
  'c2000000-0000-0000-0000-000000000001','MultipleChoice','Hidden','Standard',now(),now());
insert into public.exam_sessions(
  id,organization_id,exam_id,class_id,room_code,status,started_at,access_mode,admission_mode,
  auto_approve,accepting_participants,delivery_type,supervision_mode,quiz_result_policy,
  exam_version,created_at,updated_at)
values (
  'c2400000-0000-0000-0000-000000000001','c2100000-0000-0000-0000-000000000001',
  'c2300000-0000-0000-0000-000000000001','c2200000-0000-0000-0000-000000000001',
  'S32001','Finished','2026-08-07 02:00+00','PublicCloud','ClassMembersOnly',false,false,
  'MultipleChoice','Standard','Hidden',1,now(),now());
insert into public.session_participants(
  id,organization_id,session_id,user_id,student_code,display_name,device_id,status,
  joined_at,download_status,submission_status,extra_time_minutes,resubmit_allowed,
  source_mode,created_at,updated_at)
values (
  'c2500000-0000-0000-0000-000000000001','c2100000-0000-0000-0000-000000000001',
  'c2400000-0000-0000-0000-000000000001','c2000000-0000-0000-0000-000000000002',
  'SCHEMA32','Schema 32 Student','schema32-device','Approved',now(),
  'Completed','Submitted',0,false,'PublicCloud',now(),now());
insert into public.quiz_questions(
  id,organization_id,exam_id,version,sort_order,question_text,points,multiple,created_at,updated_at)
values (
  'c2600000-0000-0000-0000-000000000001','c2100000-0000-0000-0000-000000000001',
  'c2300000-0000-0000-0000-000000000001',1,1,'Schema 32 question',10,false,now(),now());
insert into public.quiz_choices(
  id,organization_id,question_id,sort_order,choice_text,is_correct,created_at,updated_at)
values (
  'c2700000-0000-4000-8000-000000000001','c2100000-0000-0000-0000-000000000001',
  'c2600000-0000-0000-0000-000000000001',1,'Schema 32 choice',true,now(),now());
insert into public.quiz_attempts(
  id,organization_id,session_id,participant_id,attempt_number,exam_version,result_policy,status,
  started_at,deadline_at,finalized_at,auto_score,score,max_score,grading_status,general_comment,
  graded_at,returned_at,snapshot_json,source_mode,created_at,updated_at)
values (
  'c2800000-0000-0000-0000-000000000001','c2100000-0000-0000-0000-000000000001',
  'c2400000-0000-0000-0000-000000000001','c2500000-0000-0000-0000-000000000001',1,1,
  'Hidden','Finalized','2026-08-07 02:05+00','2026-08-07 02:35+00','2026-08-07 02:20+00',
  10,10,10,'Graded','Preserve comment','2026-08-07 02:20+00',null,
  '[{"id":"c2600000-0000-0000-0000-000000000001","points":10,"choices":[{"id":"c2700000-0000-4000-8000-000000000001"}]}]'::jsonb,
  'PublicCloud','2026-08-07 02:05+00','2026-08-07 02:20+00');
insert into public.quiz_answers(
  id,organization_id,attempt_id,question_id,choice_ids,revision,client_updated_at,
  source_mode,created_at,updated_at)
values (
  'c2900000-0000-0000-0000-000000000001','c2100000-0000-0000-0000-000000000001',
  'c2800000-0000-0000-0000-000000000001','c2600000-0000-0000-0000-000000000001',
  '["c2700000-0000-4000-8000-000000000001"]',7,'2026-08-07 02:19+00',
  'PublicCloud','2026-08-07 02:05+00','2026-08-07 02:19+00');
