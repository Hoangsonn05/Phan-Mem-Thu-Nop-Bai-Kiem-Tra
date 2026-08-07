do $fixture_precondition$
begin
  if (select max(version) from supabase_migrations.schema_migrations)
       <> '20260806120000'
     or (select schema_version from public.examtransfer_cloud_meta where id=1)
       <> 30 then
    raise exception 'SCHEMA30_FIXTURE_WRONG_MIGRATION_TARGET';
  end if;
end
$fixture_precondition$;

insert into auth.users(id,email) values
  ('93000000-0000-0000-0000-000000000001','schema30-student@example.test');
insert into public.organizations(id,name) values
  ('93100000-0000-0000-0000-000000000001','Schema 30 Quiz Answer Org');
insert into public.profiles(
  id,organization_id,display_name,role,username,student_code,is_active,date_of_birth)
values ('93000000-0000-0000-0000-000000000001','93100000-0000-0000-0000-000000000001',
  'Schema 30 Student','Student','SCHEMA30','SCHEMA30',true,'2008-01-01');
insert into public.classes(
  id,organization_id,name,code,school_year,status,access_mode,created_by,created_at,updated_at)
values ('93200000-0000-0000-0000-000000000001','93100000-0000-0000-0000-000000000001',
  'Schema 30 Class','S30','2026','Active','Public',
  '93000000-0000-0000-0000-000000000001',now(),now());
insert into public.exams(
  id,organization_id,class_id,title,subject,duration_minutes,status,version,created_by,
  delivery_type,quiz_result_policy,supervision_mode,created_at,updated_at)
values ('93300000-0000-0000-0000-000000000001','93100000-0000-0000-0000-000000000001',
  '93200000-0000-0000-0000-000000000001','Schema 30 Quiz','Test',30,'Published',1,
  '93000000-0000-0000-0000-000000000001','MultipleChoice','Hidden','Standard',now(),now());
insert into public.exam_sessions(
  id,organization_id,exam_id,class_id,room_code,status,started_at,access_mode,admission_mode,
  auto_approve,accepting_participants,delivery_type,supervision_mode,quiz_result_policy,
  exam_version,created_at,updated_at)
values ('93400000-0000-0000-0000-000000000001','93100000-0000-0000-0000-000000000001',
  '93300000-0000-0000-0000-000000000001',null,'S30ANS','InProgress',now(),
  'PublicCloud','OpenRequest',true,true,'MultipleChoice','Standard','Hidden',1,now(),now());
insert into public.session_participants(
  id,organization_id,session_id,user_id,student_code,display_name,status,source_mode,created_at,updated_at)
values ('93500000-0000-0000-0000-000000000001','93100000-0000-0000-0000-000000000001',
  '93400000-0000-0000-0000-000000000001','93000000-0000-0000-0000-000000000001',
  'SCHEMA30','Schema 30 Student','Approved','PublicCloud',now(),now());
insert into public.quiz_questions(
  id,organization_id,exam_id,version,sort_order,question_text,points,multiple)
values ('93600000-0000-0000-0000-000000000001','93100000-0000-0000-0000-000000000001',
  '93300000-0000-0000-0000-000000000001',1,1,'Preserved question',10,false);
insert into public.quiz_choices(
  id,organization_id,question_id,sort_order,choice_text,is_correct)
values
  ('93700000-0000-4000-8000-000000000001','93100000-0000-0000-0000-000000000001',
   '93600000-0000-0000-0000-000000000001',1,'A',true),
  ('93700000-0000-4000-8000-000000000002','93100000-0000-0000-0000-000000000001',
   '93600000-0000-0000-0000-000000000001',2,'B',false);
insert into public.quiz_attempts(
  id,organization_id,session_id,participant_id,exam_version,result_policy,status,
  started_at,deadline_at,max_score,snapshot_json,source_mode,created_at,updated_at)
values ('93800000-0000-0000-0000-000000000001','93100000-0000-0000-0000-000000000001',
  '93400000-0000-0000-0000-000000000001','93500000-0000-0000-0000-000000000001',
  1,'Hidden','InProgress',now(),now()+interval '30 minutes',10,
  '[{"id":"93600000-0000-0000-0000-000000000001","sortOrder":1,"questionText":"Preserved question","points":10,"multiple":false,"choices":[{"id":"93700000-0000-4000-8000-000000000001","sortOrder":1,"choiceText":"A"},{"id":"93700000-0000-4000-8000-000000000002","sortOrder":2,"choiceText":"B"}]}]'::jsonb,
  'PublicCloud',now(),now());
insert into public.quiz_answers(
  id,organization_id,attempt_id,question_id,choice_ids,revision,client_updated_at,
  source_mode,created_at,updated_at)
values ('93900000-0000-0000-0000-000000000001','93100000-0000-0000-0000-000000000001',
  '93800000-0000-0000-0000-000000000001','93600000-0000-0000-0000-000000000001',
  '[]'::jsonb,7,now(),'PublicCloud',now(),now());
