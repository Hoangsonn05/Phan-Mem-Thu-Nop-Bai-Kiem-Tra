begin;
create extension if not exists pgtap with schema extensions;
set local search_path = public, extensions;
select plan(8);

insert into auth.users(id,email) values
  ('83000000-0000-0000-0000-000000000001','pc2-snapshot-student@example.test');
insert into public.organizations(id,name) values
  ('83100000-0000-0000-0000-000000000001','PC2 Snapshot Org');
insert into public.profiles(
  id,organization_id,display_name,role,username,student_code,is_active,date_of_birth)
values ('83000000-0000-0000-0000-000000000001','83100000-0000-0000-0000-000000000001',
  'PC2 Snapshot Student','Student','pc2-snapshot','PC2-SNAPSHOT',true,'2008-01-01');
insert into public.classes(
  id,organization_id,name,code,school_year,status,access_mode,created_by,created_at,updated_at)
values ('83200000-0000-0000-0000-000000000001','83100000-0000-0000-0000-000000000001',
  'PC2 Snapshot Class','PC2S','2026','Active','Public',
  '83000000-0000-0000-0000-000000000001',now(),now());
insert into public.exams(
  id,organization_id,class_id,title,subject,duration_minutes,status,version,created_by,
  delivery_type,quiz_result_policy,supervision_mode,created_at,updated_at)
values ('83300000-0000-0000-0000-000000000001','83100000-0000-0000-0000-000000000001',
  '83200000-0000-0000-0000-000000000001','PC2 Snapshot Quiz','Test',30,'Published',1,
  '83000000-0000-0000-0000-000000000001','MultipleChoice','Hidden','Standard',now(),now());
insert into public.exam_sessions(
  id,organization_id,exam_id,class_id,room_code,status,started_at,access_mode,admission_mode,
  auto_approve,accepting_participants,delivery_type,supervision_mode,quiz_result_policy,
  exam_version,created_at,updated_at)
values ('83400000-0000-0000-0000-000000000001','83100000-0000-0000-0000-000000000001',
  '83300000-0000-0000-0000-000000000001',null,
  'PC2SNP','InProgress',now(),'PublicCloud','OpenRequest',true,true,
  'MultipleChoice','Standard','Hidden',1,now(),now());
insert into public.session_participants(
  id,organization_id,session_id,user_id,student_code,display_name,status,source_mode,created_at,updated_at)
values ('83500000-0000-0000-0000-000000000001','83100000-0000-0000-0000-000000000001',
  '83400000-0000-0000-0000-000000000001','83000000-0000-0000-0000-000000000001',
  'PC2-SNAPSHOT','PC2 Snapshot Student','Approved','PublicCloud',now(),now());
insert into public.quiz_questions(
  id,organization_id,exam_id,version,sort_order,question_text,points,multiple)
values
  ('83600000-0000-0000-0000-000000000001','83100000-0000-0000-0000-000000000001',
   '83300000-0000-0000-0000-000000000001',2,1,'Snapshot single',5,false),
  ('83600000-0000-0000-0000-000000000002','83100000-0000-0000-0000-000000000001',
   '83300000-0000-0000-0000-000000000001',2,2,'Snapshot multiple',5,true),
  ('83600000-0000-0000-0000-000000000003','83100000-0000-0000-0000-000000000001',
   '83300000-0000-0000-0000-000000000001',1,3,'New canonical question',5,false);
insert into public.quiz_choices(
  id,organization_id,question_id,sort_order,choice_text,is_correct)
values
  ('83700000-0000-4000-8000-000000000001','83100000-0000-0000-0000-000000000001',
   '83600000-0000-0000-0000-000000000001',1,'Old A',true),
  ('83700000-0000-4000-8000-000000000005','83100000-0000-0000-0000-000000000001',
   '83600000-0000-0000-0000-000000000001',2,'Old B',false),
  ('83700000-0000-4000-8000-000000000002','83100000-0000-0000-0000-000000000001',
   '83600000-0000-0000-0000-000000000002',1,'Old M1',true),
  ('83700000-0000-4000-8000-000000000003','83100000-0000-0000-0000-000000000001',
   '83600000-0000-0000-0000-000000000002',2,'Old M2',true),
  ('83700000-0000-4000-8000-000000000004','83100000-0000-0000-0000-000000000001',
   '83600000-0000-0000-0000-000000000003',1,'New only',true);
insert into public.quiz_attempts(
  id,organization_id,session_id,participant_id,exam_version,result_policy,status,
  started_at,deadline_at,max_score,snapshot_json,source_mode,created_at,updated_at)
values ('83800000-0000-0000-0000-000000000001','83100000-0000-0000-0000-000000000001',
  '83400000-0000-0000-0000-000000000001','83500000-0000-0000-0000-000000000001',
  1,'Hidden','InProgress',now(),now()+interval '30 minutes',10,
  '[{"id":"83600000-0000-0000-0000-000000000001","sortOrder":1,"questionText":"Snapshot single","points":5,"multiple":false,"choices":[{"id":"83700000-0000-4000-8000-000000000001","sortOrder":1,"choiceText":"Old A"},{"id":"83700000-0000-4000-8000-000000000005","sortOrder":2,"choiceText":"Old B"}]},{"id":"83600000-0000-0000-0000-000000000002","sortOrder":2,"questionText":"Snapshot multiple","points":5,"multiple":true,"choices":[{"id":"83700000-0000-4000-8000-000000000002","sortOrder":1,"choiceText":"Old M1"},{"id":"83700000-0000-4000-8000-000000000003","sortOrder":2,"choiceText":"Old M2"}]}]'::jsonb,
  'PublicCloud',now(),now());

set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"83000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select lives_ok($$select public.save_public_quiz_answers(
  '83800000-0000-0000-0000-000000000001','83600000-0000-0000-0000-000000000001',
  '["83700000-0000-4000-8000-000000000001"]',1,now())$$,
  'a choice in the persisted attempt snapshot remains valid after canonical version changes');
reset role;
select is((select revision from public.quiz_answers where
  attempt_id='83800000-0000-0000-0000-000000000001'
  and question_id='83600000-0000-0000-0000-000000000001'),1::bigint,
  'snapshot-authorized answer is persisted');
set local role authenticated;
select lives_ok($$select public.save_public_quiz_answers(
  '83800000-0000-0000-0000-000000000001','83600000-0000-0000-0000-000000000002',
  '["83700000-0000-4000-8000-000000000002","83700000-0000-4000-8000-000000000003"]',1,now())$$,
  'multiple choice accepts multiple snapshot choices');
reset role;
select is((select jsonb_array_length(choice_ids) from public.quiz_answers where
  attempt_id='83800000-0000-0000-0000-000000000001'
  and question_id='83600000-0000-0000-0000-000000000002'),2,
  'multiple snapshot choices are persisted');
set local role authenticated;
select throws_ok($$select public.save_public_quiz_answers(
  '83800000-0000-0000-0000-000000000001','83600000-0000-0000-0000-000000000003',
  '["83700000-0000-4000-8000-000000000004"]',1,now())$$,
  'P0002','QUIZ_QUESTION_NOT_FOUND','a new canonical question outside the snapshot is rejected');
select throws_ok($$select public.save_public_quiz_answers(
  '83800000-0000-0000-0000-000000000001','83600000-0000-0000-0000-000000000001',
  '["83700000-0000-4000-8000-000000000004"]',2,now())$$,
  '22023','QUIZ_CHOICE_INVALID','a choice outside the snapshot question is rejected');
reset role;
update public.quiz_attempts set snapshot_json='{}'::jsonb
where id='83800000-0000-0000-0000-000000000001';
set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"83000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select throws_ok($$select public.save_public_quiz_answers(
  '83800000-0000-0000-0000-000000000001','83600000-0000-0000-0000-000000000001',
  '[]',2,now())$$,
  '55000','QUIZ_ATTEMPT_SNAPSHOT_INVALID','invalid snapshot fails closed');
reset role;
select is((select count(*)::integer from public.quiz_answers where
  attempt_id='83800000-0000-0000-0000-000000000001'),2,
  'invalid snapshot does not mutate existing answers');

select * from finish();
rollback;
