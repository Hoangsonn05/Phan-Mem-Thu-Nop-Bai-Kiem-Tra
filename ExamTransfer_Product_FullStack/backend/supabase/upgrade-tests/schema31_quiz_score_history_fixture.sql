do $fixture_precondition$
begin
  if (select max(version) from supabase_migrations.schema_migrations)
       <> '20260807044053'
     or (select schema_version from public.examtransfer_cloud_meta where id=1)
       <> 31 then
    raise exception 'SCHEMA31_FIXTURE_WRONG_MIGRATION_TARGET';
  end if;
end
$fixture_precondition$;

insert into auth.users(id,email) values
  ('b3000000-0000-0000-0000-000000000001','schema31-teacher@example.test'),
  ('b3000000-0000-0000-0000-000000000002','schema31-student@example.test');
insert into public.organizations(id,name) values
  ('b3100000-0000-0000-0000-000000000001','Schema 31 Score History Org');
insert into public.profiles(
  id,organization_id,display_name,role,username,student_code,is_active,date_of_birth)
values
  ('b3000000-0000-0000-0000-000000000001','b3100000-0000-0000-0000-000000000001',
   'Schema 31 Teacher','Teacher','schema31-teacher',null,true,null),
  ('b3000000-0000-0000-0000-000000000002','b3100000-0000-0000-0000-000000000001',
   'Schema 31 Student','Student','SCHEMA31','SCHEMA31',true,'2008-01-01');
insert into public.classes(
  id,organization_id,name,code,school_year,status,access_mode,created_by,created_at,updated_at)
values ('b3200000-0000-0000-0000-000000000001','b3100000-0000-0000-0000-000000000001',
  'Schema 31 Class','S31','2026','Active','Public',
  'b3000000-0000-0000-0000-000000000001',now(),now());
insert into public.exams(
  id,organization_id,class_id,title,subject,duration_minutes,status,version,created_by,
  delivery_type,quiz_result_policy,supervision_mode,created_at,updated_at)
values
  ('b3300000-0000-0000-0000-000000000001','b3100000-0000-0000-0000-000000000001',
   'b3200000-0000-0000-0000-000000000001','Schema 31 Show Quiz','Test',30,'Published',1,
   'b3000000-0000-0000-0000-000000000001','MultipleChoice','ShowAfterSubmission','Standard',now(),now()),
  ('b3300000-0000-0000-0000-000000000002','b3100000-0000-0000-0000-000000000001',
   'b3200000-0000-0000-0000-000000000001','Schema 31 Hidden Quiz','Test',30,'Published',1,
   'b3000000-0000-0000-0000-000000000001','MultipleChoice','Hidden','Standard',now(),now());
insert into public.exam_sessions(
  id,organization_id,exam_id,class_id,room_code,status,started_at,access_mode,admission_mode,
  auto_approve,accepting_participants,delivery_type,supervision_mode,quiz_result_policy,
  exam_version,created_at,updated_at)
values
  ('b3400000-0000-0000-0000-000000000001','b3100000-0000-0000-0000-000000000001',
   'b3300000-0000-0000-0000-000000000001','b3200000-0000-0000-0000-000000000001',
   'S31SHOW','Finished','2026-08-07 00:00+00','PublicCloud','ClassMembersOnly',false,false,
   'MultipleChoice','Standard','ShowAfterSubmission',1,now(),now()),
  ('b3400000-0000-0000-0000-000000000002','b3100000-0000-0000-0000-000000000001',
   'b3300000-0000-0000-0000-000000000002','b3200000-0000-0000-0000-000000000001',
   'S31HIDE','Finished','2026-08-07 00:00+00','PublicCloud','ClassMembersOnly',false,false,
   'MultipleChoice','Standard','Hidden',1,now(),now());
insert into public.session_participants(
  id,organization_id,session_id,user_id,student_code,display_name,device_id,status,
  joined_at,download_status,submission_status,extra_time_minutes,resubmit_allowed,
  source_mode,created_at,updated_at)
values
  ('b3500000-0000-0000-0000-000000000001','b3100000-0000-0000-0000-000000000001',
   'b3400000-0000-0000-0000-000000000001','b3000000-0000-0000-0000-000000000002',
   'SCHEMA31','Schema 31 Student','schema31-show','Approved',now(),'Completed','Submitted',0,false,
   'PublicCloud',now(),now()),
  ('b3500000-0000-0000-0000-000000000002','b3100000-0000-0000-0000-000000000001',
   'b3400000-0000-0000-0000-000000000002','b3000000-0000-0000-0000-000000000002',
   'SCHEMA31','Schema 31 Student','schema31-hidden','Approved',now(),'Completed','Submitted',0,false,
   'PublicCloud',now(),now());
insert into public.quiz_questions(
  id,organization_id,exam_id,version,sort_order,question_text,points,multiple)
values
  ('b3600000-0000-0000-0000-000000000001','b3100000-0000-0000-0000-000000000001',
   'b3300000-0000-0000-0000-000000000001',1,1,'Schema 31 weighted 7.5',7.50,false),
  ('b3600000-0000-0000-0000-000000000002','b3100000-0000-0000-0000-000000000001',
   'b3300000-0000-0000-0000-000000000001',1,2,'Schema 31 weighted 2.5',2.50,false),
  ('b3600000-0000-0000-0000-000000000003','b3100000-0000-0000-0000-000000000001',
   'b3300000-0000-0000-0000-000000000002',1,1,'Schema 31 hidden 10',10.00,false);
insert into public.quiz_choices(
  id,organization_id,question_id,sort_order,choice_text,is_correct)
values
  ('b3700000-0000-4000-8000-000000000001','b3100000-0000-0000-0000-000000000001','b3600000-0000-0000-0000-000000000001',1,'Correct 7.5',true),
  ('b3700000-0000-4000-8000-000000000002','b3100000-0000-0000-0000-000000000001','b3600000-0000-0000-0000-000000000001',2,'Wrong 7.5',false),
  ('b3700000-0000-4000-8000-000000000003','b3100000-0000-0000-0000-000000000001','b3600000-0000-0000-0000-000000000002',1,'Correct 2.5',true),
  ('b3700000-0000-4000-8000-000000000004','b3100000-0000-0000-0000-000000000001','b3600000-0000-0000-0000-000000000002',2,'Wrong 2.5',false),
  ('b3700000-0000-4000-8000-000000000005','b3100000-0000-0000-0000-000000000001','b3600000-0000-0000-0000-000000000003',1,'Correct 10',true),
  ('b3700000-0000-4000-8000-000000000006','b3100000-0000-0000-0000-000000000001','b3600000-0000-0000-0000-000000000003',2,'Wrong 10',false);
insert into public.quiz_attempts(
  id,organization_id,session_id,participant_id,attempt_number,exam_version,result_policy,status,
  started_at,deadline_at,finalized_at,auto_score,score,max_score,grading_status,graded_at,
  returned_at,snapshot_json,source_mode,created_at,updated_at)
values
  ('b3800000-0000-0000-0000-000000000001','b3100000-0000-0000-0000-000000000001',
   'b3400000-0000-0000-0000-000000000001','b3500000-0000-0000-0000-000000000001',1,1,
   'ShowAfterSubmission','Finalized','2026-08-07 00:10+00','2026-08-07 00:40+00','2026-08-07 00:22:34+00',
   7.50,7.50,10,'Graded','2026-08-07 00:22:34+00',null,
   '[{"id":"b3600000-0000-0000-0000-000000000001","sortOrder":1,"questionText":"Schema 31 weighted 7.5","points":7.50,"multiple":false,"choices":[{"id":"b3700000-0000-4000-8000-000000000001","sortOrder":1,"choiceText":"Correct 7.5"},{"id":"b3700000-0000-4000-8000-000000000002","sortOrder":2,"choiceText":"Wrong 7.5"}]},{"id":"b3600000-0000-0000-0000-000000000002","sortOrder":2,"questionText":"Schema 31 weighted 2.5","points":2.50,"multiple":false,"choices":[{"id":"b3700000-0000-4000-8000-000000000003","sortOrder":1,"choiceText":"Correct 2.5"},{"id":"b3700000-0000-4000-8000-000000000004","sortOrder":2,"choiceText":"Wrong 2.5"}]}]'::jsonb,
   'PublicCloud',now(),now()),
  ('b3800000-0000-0000-0000-000000000002','b3100000-0000-0000-0000-000000000001',
   'b3400000-0000-0000-0000-000000000002','b3500000-0000-0000-0000-000000000002',1,1,
   'Hidden','Finalized','2026-08-07 00:20+00','2026-08-07 00:50+00','2026-08-07 00:30+00',
   10,10,10,'Graded','2026-08-07 00:30+00',null,
   '[{"id":"b3600000-0000-0000-0000-000000000003","sortOrder":1,"questionText":"Schema 31 hidden 10","points":10.00,"multiple":false,"choices":[{"id":"b3700000-0000-4000-8000-000000000005","sortOrder":1,"choiceText":"Correct 10"},{"id":"b3700000-0000-4000-8000-000000000006","sortOrder":2,"choiceText":"Wrong 10"}]}]'::jsonb,
   'PublicCloud',now(),now());
insert into public.quiz_answers(
  id,organization_id,attempt_id,question_id,choice_ids,revision,client_updated_at,
  source_mode,created_at,updated_at)
values
  ('b3900000-0000-0000-0000-000000000001','b3100000-0000-0000-0000-000000000001','b3800000-0000-0000-0000-000000000001','b3600000-0000-0000-0000-000000000001','["b3700000-0000-4000-8000-000000000001"]',3,now(),'PublicCloud',now(),now()),
  ('b3900000-0000-0000-0000-000000000002','b3100000-0000-0000-0000-000000000001','b3800000-0000-0000-0000-000000000001','b3600000-0000-0000-0000-000000000002','["b3700000-0000-4000-8000-000000000004"]',4,now(),'PublicCloud',now(),now()),
  ('b3900000-0000-0000-0000-000000000003','b3100000-0000-0000-0000-000000000001','b3800000-0000-0000-0000-000000000002','b3600000-0000-0000-0000-000000000003','["b3700000-0000-4000-8000-000000000005"]',5,now(),'PublicCloud',now(),now());
