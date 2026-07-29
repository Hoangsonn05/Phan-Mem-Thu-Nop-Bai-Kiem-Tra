do $fixture_precondition$
begin
  if (select max(version) from supabase_migrations.schema_migrations)
       <> '20260728113000'
     or (select schema_version from public.examtransfer_cloud_meta where id=1)
       <> 22 then
    raise exception 'SCHEMA22_FIXTURE_WRONG_MIGRATION_TARGET';
  end if;
end
$fixture_precondition$;

insert into auth.users(id,email) values
  ('52000000-0000-0000-0000-000000000001','schema22-teacher@example.test'),
  ('52000000-0000-0000-0000-000000000002','schema22-student@example.test')
on conflict (id) do nothing;
insert into public.organizations(id,name) values
  ('52000000-0000-0000-0000-000000000000','Schema 22 Upgrade Org');
insert into public.profiles(
  id,organization_id,display_name,role,username,student_code,is_active,date_of_birth)
values
  ('52000000-0000-0000-0000-000000000001',
   '52000000-0000-0000-0000-000000000000',
   'Schema 22 Teacher','Teacher','schema22-teacher',null,true,null),
  ('52000000-0000-0000-0000-000000000002',
   '52000000-0000-0000-0000-000000000000',
   'Schema 22 Student','Student','S22','S22',true,'2008-01-01');
insert into public.classes(
  id,organization_id,name,code,school_year,status,access_mode,created_by,created_at,updated_at)
values (
  '52100000-0000-0000-0000-000000000000',
  '52000000-0000-0000-0000-000000000000',
  'Schema 22 Class','S22','2026','Active','Public',
  '52000000-0000-0000-0000-000000000001',now(),now());
insert into public.exams(
  id,organization_id,class_id,title,subject,duration_minutes,status,version,
  created_by,delivery_type,quiz_result_policy,supervision_mode,created_at,updated_at)
values (
  '52200000-0000-0000-0000-000000000000',
  '52000000-0000-0000-0000-000000000000',
  '52100000-0000-0000-0000-000000000000',
  'Schema 22 Quiz','IT',60,'Published',1,
  '52000000-0000-0000-0000-000000000001',
  'MultipleChoice','Hidden','Standard',now(),now());
insert into public.exam_sessions(
  id,organization_id,exam_id,class_id,room_code,status,started_at,access_mode,
  auto_approve,accepting_participants,delivery_type,supervision_mode,
  quiz_result_policy,exam_version,created_at,updated_at)
values (
  '52300000-0000-0000-0000-000000000000',
  '52000000-0000-0000-0000-000000000000',
  '52200000-0000-0000-0000-000000000000',
  '52100000-0000-0000-0000-000000000000',
  'S22UPG','Finished',now()-interval '1 hour','PublicCloud',
  false,false,'MultipleChoice','Standard','Hidden',1,now(),now());
insert into public.session_participants(
  id,organization_id,session_id,user_id,student_code,display_name,device_id,status,
  joined_at,download_status,submission_status,extra_time_minutes,resubmit_allowed,
  source_mode,created_at,updated_at)
values (
  '52400000-0000-0000-0000-000000000000',
  '52000000-0000-0000-0000-000000000000',
  '52300000-0000-0000-0000-000000000000',
  '52000000-0000-0000-0000-000000000002',
  'S22','Schema 22 Student','schema22-device','Approved',
  now(),'Completed','Submitted',0,false,'PublicCloud',now(),now());
insert into public.public_device_connections(
  id,organization_id,session_id,participant_id,user_id,device_id,
  connection_state,heartbeat_at,source_mode,created_at,updated_at)
values (
  '52500000-0000-0000-0000-000000000000',
  '52000000-0000-0000-0000-000000000000',
  '52300000-0000-0000-0000-000000000000',
  '52400000-0000-0000-0000-000000000000',
  '52000000-0000-0000-0000-000000000002',
  'schema22-device','Online',now(),'PublicCloud',now(),now());
insert into public.quiz_attempts(
  id,organization_id,session_id,participant_id,exam_version,result_policy,status,
  started_at,deadline_at,finalized_at,auto_score,score,max_score,grading_status,
  general_comment,grader_id,graded_at,returned_at,snapshot_json,source_mode,
  created_at,updated_at)
values (
  '52600000-0000-0000-0000-000000000000',
  '52000000-0000-0000-0000-000000000000',
  '52300000-0000-0000-0000-000000000000',
  '52400000-0000-0000-0000-000000000000',
  1,'Hidden','Finalized',now()-interval '30 minutes',now(),
  now()-interval '20 minutes',8.25,8.00,10.00,'Returned',
  'Schema 22 preserved comment',
  '52000000-0000-0000-0000-000000000001',
  now()-interval '20 minutes',now()-interval '10 minutes',
  '[]'::jsonb,'PublicCloud',now(),now());

do $fixture_loaded$
begin
  if not exists (
    select 1 from public.quiz_attempts
    where id='52600000-0000-0000-0000-000000000000'
      and grading_status='Returned'
      and score=8.00
      and returned_at is not null
  ) then
    raise exception 'SCHEMA22_FIXTURE_LOAD_FAILED';
  end if;
end
$fixture_loaded$;
