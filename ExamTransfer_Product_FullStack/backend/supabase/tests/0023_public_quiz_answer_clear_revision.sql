begin;
create extension if not exists pgtap with schema extensions;
set local search_path = public, extensions;
select plan(12);

insert into auth.users(id,email) values
  ('82000000-0000-0000-0000-000000000001','pc2-clear-student@example.test'),
  ('82000000-0000-0000-0000-000000000002','pc2-clear-peer@example.test');
insert into public.organizations(id,name) values
  ('82100000-0000-0000-0000-000000000001','PC2 Clear Org');
insert into public.profiles(
  id,organization_id,display_name,role,username,student_code,is_active,date_of_birth)
values
  ('82000000-0000-0000-0000-000000000001','82100000-0000-0000-0000-000000000001',
   'PC2 Student','Student','pc2-clear','PC2-CLEAR',true,'2008-01-01'),
  ('82000000-0000-0000-0000-000000000002','82100000-0000-0000-0000-000000000001',
   'PC2 Peer','Student','pc2-peer','PC2-PEER',true,'2008-01-01');
insert into public.classes(
  id,organization_id,name,code,school_year,status,access_mode,created_by,created_at,updated_at)
values ('82200000-0000-0000-0000-000000000001','82100000-0000-0000-0000-000000000001',
  'PC2 Class','PC2C','2026','Active','Public',
  '82000000-0000-0000-0000-000000000001',now(),now());
insert into public.exams(
  id,organization_id,class_id,title,subject,duration_minutes,status,version,created_by,
  delivery_type,quiz_result_policy,supervision_mode,created_at,updated_at)
values ('82300000-0000-0000-0000-000000000001','82100000-0000-0000-0000-000000000001',
  '82200000-0000-0000-0000-000000000001','PC2 Clear Quiz','Test',30,'Published',1,
  '82000000-0000-0000-0000-000000000001','MultipleChoice','Hidden','Standard',now(),now());
insert into public.exam_sessions(
  id,organization_id,exam_id,class_id,room_code,status,started_at,access_mode,admission_mode,
  auto_approve,accepting_participants,delivery_type,supervision_mode,quiz_result_policy,
  exam_version,created_at,updated_at)
values ('82400000-0000-0000-0000-000000000001','82100000-0000-0000-0000-000000000001',
  '82300000-0000-0000-0000-000000000001',null,
  'PC2CLR','InProgress',now(),'PublicCloud','OpenRequest',true,true,
  'MultipleChoice','Standard','Hidden',1,now(),now());
insert into public.session_participants(
  id,organization_id,session_id,user_id,student_code,display_name,status,source_mode,created_at,updated_at)
values ('82500000-0000-0000-0000-000000000001','82100000-0000-0000-0000-000000000001',
  '82400000-0000-0000-0000-000000000001','82000000-0000-0000-0000-000000000001',
  'PC2-CLEAR','PC2 Student','Approved','PublicCloud',now(),now());
insert into public.quiz_questions(
  id,organization_id,exam_id,version,sort_order,question_text,points,multiple)
values ('82600000-0000-0000-0000-000000000001','82100000-0000-0000-0000-000000000001',
  '82300000-0000-0000-0000-000000000001',1,1,'PC2 single choice',10,false);
insert into public.quiz_choices(
  id,organization_id,question_id,sort_order,choice_text,is_correct)
values
  ('82700000-0000-4000-8000-000000000001','82100000-0000-0000-0000-000000000001',
   '82600000-0000-0000-0000-000000000001',1,'A',true),
  ('82700000-0000-4000-8000-000000000002','82100000-0000-0000-0000-000000000001',
   '82600000-0000-0000-0000-000000000001',2,'B',false);
insert into public.quiz_attempts(
  id,organization_id,session_id,participant_id,exam_version,result_policy,status,
  started_at,deadline_at,max_score,snapshot_json,source_mode,created_at,updated_at)
values ('82800000-0000-0000-0000-000000000001','82100000-0000-0000-0000-000000000001',
  '82400000-0000-0000-0000-000000000001','82500000-0000-0000-0000-000000000001',
  1,'Hidden','InProgress',now(),now()+interval '30 minutes',10,
  '[{"id":"82600000-0000-0000-0000-000000000001","sortOrder":1,"questionText":"PC2 single choice","points":10,"multiple":false,"choices":[{"id":"82700000-0000-4000-8000-000000000001","sortOrder":1,"choiceText":"A"},{"id":"82700000-0000-4000-8000-000000000002","sortOrder":2,"choiceText":"B"}]}]'::jsonb,
  'PublicCloud',now(),now());

set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"82000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select lives_ok($$select public.save_public_quiz_answers(
  '82800000-0000-0000-0000-000000000001','82600000-0000-0000-0000-000000000001',
  '["82700000-0000-4000-8000-000000000001"]',1,now())$$,
  'selected answer revision 1 is accepted');
select lives_ok($$select public.save_public_quiz_answers(
  '82800000-0000-0000-0000-000000000001','82600000-0000-0000-0000-000000000001',
  '[]',2,now())$$,
  'clearing the final choice is accepted');
reset role;
select is((select choice_ids from public.quiz_answers where
  attempt_id='82800000-0000-0000-0000-000000000001'), '[]'::jsonb,
  'clear persists an empty choice array');
select is((select revision from public.quiz_answers where
  attempt_id='82800000-0000-0000-0000-000000000001'), 2::bigint,
  'clear persists the newer revision');
set local role authenticated;
select is(public.save_public_quiz_answers(
  '82800000-0000-0000-0000-000000000001','82600000-0000-0000-0000-000000000001',
  '["82700000-0000-4000-8000-000000000001"]',1,now()), 2::bigint,
  'stale revision returns the authoritative revision');
reset role;
select is((select choice_ids from public.quiz_answers where
  attempt_id='82800000-0000-0000-0000-000000000001'), '[]'::jsonb,
  'stale revision cannot restore the cleared answer');
set local role authenticated;
select is(public.save_public_quiz_answers(
  '82800000-0000-0000-0000-000000000001','82600000-0000-0000-0000-000000000001',
  '[]',2,now()), 2::bigint,
  'equal revision with the same payload is idempotent');
select is(public.save_public_quiz_answers(
  '82800000-0000-0000-0000-000000000001','82600000-0000-0000-0000-000000000001',
  '["82700000-0000-4000-8000-000000000002"]',2,now()), 2::bigint,
  'equal revision with different payload does not overwrite');
reset role;
select is((select choice_ids from public.quiz_answers where
  attempt_id='82800000-0000-0000-0000-000000000001'), '[]'::jsonb,
  'equal revision conflict preserves the authoritative payload');
set local role authenticated;
select throws_ok($$select public.save_public_quiz_answers(
  '82800000-0000-0000-0000-000000000001','82600000-0000-0000-0000-000000000001',
  '["82700000-0000-4000-8000-000000000001","82700000-0000-4000-8000-000000000001"]',3,now())$$,
  '22023','QUIZ_CHOICE_DUPLICATE','duplicate choice ids are rejected');
select throws_ok($$select public.save_public_quiz_answers(
  '82800000-0000-0000-0000-000000000001','82600000-0000-0000-0000-000000000001',
  '["82700000-0000-4000-8000-000000000001","82700000-0000-4000-8000-000000000002"]',3,now())$$,
  '22023','QUIZ_CHOICE_COUNT_INVALID','single choice rejects more than one choice');
select set_config('request.jwt.claims',
  '{"sub":"82000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
select throws_ok($$select public.save_public_quiz_answers(
  '82800000-0000-0000-0000-000000000001','82600000-0000-0000-0000-000000000001',
  '[]',3,now())$$,
  'P0002','PUBLIC_QUIZ_ATTEMPT_NOT_FOUND','another student cannot save the attempt');

select * from finish();
rollback;
