begin;
create extension if not exists pgtap with schema extensions;
set local search_path = public, extensions;
select plan(33);

select is((select schema_version from public.examtransfer_cloud_meta where id=1),33,
  'PC3 score and history behavior is available at cloud schema 32');

insert into auth.users(id,email) values
  ('a3000000-0000-0000-0000-000000000001','pc3-teacher@example.test'),
  ('a3000000-0000-0000-0000-000000000002','pc3-show@example.test'),
  ('a3000000-0000-0000-0000-000000000003','pc3-hidden@example.test'),
  ('a3000000-0000-0000-0000-000000000004','pc3-peer@example.test'),
  ('a3000000-0000-0000-0000-000000000005','pc3-other@example.test')
on conflict (id) do nothing;
insert into public.organizations(id,name) values
  ('a3100000-0000-0000-0000-000000000001','PC3 Org'),
  ('a3100000-0000-0000-0000-000000000002','PC3 Other Org');
insert into public.profiles(
  id,organization_id,display_name,role,username,student_code,is_active,date_of_birth)
values
  ('a3000000-0000-0000-0000-000000000001','a3100000-0000-0000-0000-000000000001',
   'PC3 Teacher','Teacher','pc3-teacher',null,true,null),
  ('a3000000-0000-0000-0000-000000000002','a3100000-0000-0000-0000-000000000001',
   'PC3 Show Student','Student','PC3-SHOW','PC3-SHOW',true,'2008-01-01'),
  ('a3000000-0000-0000-0000-000000000003','a3100000-0000-0000-0000-000000000001',
   'PC3 Hidden Student','Student','PC3-HIDDEN','PC3-HIDDEN',true,'2008-01-01'),
  ('a3000000-0000-0000-0000-000000000004','a3100000-0000-0000-0000-000000000001',
   'PC3 Peer','Student','PC3-PEER','PC3-PEER',true,'2008-01-01'),
  ('a3000000-0000-0000-0000-000000000005','a3100000-0000-0000-0000-000000000002',
   'PC3 Other','Student','PC3-OTHER','PC3-OTHER',true,'2008-01-01');
insert into public.classes(
  id,organization_id,name,code,school_year,status,access_mode,created_by,created_at,updated_at)
values ('a3200000-0000-0000-0000-000000000001','a3100000-0000-0000-0000-000000000001',
  'PC3 Class','PC3CLASS','2026','Active','Public',
  'a3000000-0000-0000-0000-000000000001',now(),now());
insert into public.exams(
  id,organization_id,class_id,title,subject,duration_minutes,status,version,created_by,
  delivery_type,quiz_result_policy,supervision_mode,created_at,updated_at)
values
  ('a3300000-0000-0000-0000-000000000001','a3100000-0000-0000-0000-000000000001',
   'a3200000-0000-0000-0000-000000000001','PC3 Show Quiz','Test',60,'Published',1,
   'a3000000-0000-0000-0000-000000000001','MultipleChoice','ShowAfterSubmission','Standard',now(),now()),
  ('a3300000-0000-0000-0000-000000000002','a3100000-0000-0000-0000-000000000001',
   'a3200000-0000-0000-0000-000000000001','PC3 Hidden Quiz','Test',60,'Published',1,
   'a3000000-0000-0000-0000-000000000001','MultipleChoice','Hidden','Standard',now(),now());
insert into public.exam_sessions(
  id,organization_id,exam_id,class_id,room_code,status,started_at,access_mode,admission_mode,
  auto_approve,accepting_participants,delivery_type,supervision_mode,quiz_result_policy,
  exam_version,created_at,updated_at)
values
  ('a3400000-0000-0000-0000-000000000001','a3100000-0000-0000-0000-000000000001',
   'a3300000-0000-0000-0000-000000000001','a3200000-0000-0000-0000-000000000001',
   'PC3SHOW','InProgress','2026-08-07 00:00+00','PublicCloud','ClassMembersOnly',false,false,
   'MultipleChoice','Standard','ShowAfterSubmission',1,now(),now()),
  ('a3400000-0000-0000-0000-000000000002','a3100000-0000-0000-0000-000000000001',
   'a3300000-0000-0000-0000-000000000002','a3200000-0000-0000-0000-000000000001',
   'PC3HIDE','InProgress','2026-08-07 00:00+00','PublicCloud','ClassMembersOnly',false,false,
   'MultipleChoice','Standard','Hidden',1,now(),now()),
  ('a3400000-0000-0000-0000-000000000003','a3100000-0000-0000-0000-000000000001',
   'a3300000-0000-0000-0000-000000000001','a3200000-0000-0000-0000-000000000001',
   'PC3LEGACY','Finished','2026-08-06 21:50+00','PublicCloud','ClassMembersOnly',false,false,
   'MultipleChoice','Standard','ShowAfterSubmission',1,now(),now()),
  ('a3400000-0000-0000-0000-000000000004','a3100000-0000-0000-0000-000000000001',
   'a3300000-0000-0000-0000-000000000001','a3200000-0000-0000-0000-000000000001',
   'PC3CORRUPT','Finished','2026-08-06 19:50+00','PublicCloud','ClassMembersOnly',false,false,
   'MultipleChoice','Standard','ShowAfterSubmission',1,now(),now());
insert into public.session_participants(
  id,organization_id,session_id,user_id,student_code,display_name,device_id,status,
  joined_at,download_status,submission_status,extra_time_minutes,resubmit_allowed,
  source_mode,created_at,updated_at)
values
  ('a3500000-0000-0000-0000-000000000001','a3100000-0000-0000-0000-000000000001',
   'a3400000-0000-0000-0000-000000000001','a3000000-0000-0000-0000-000000000002',
   'PC3-SHOW','PC3 Show Student','pc3-show-device','Approved',now(),'Completed','Submitted',0,false,
   'PublicCloud',now(),now()),
  ('a3500000-0000-0000-0000-000000000002','a3100000-0000-0000-0000-000000000001',
   'a3400000-0000-0000-0000-000000000002','a3000000-0000-0000-0000-000000000003',
   'PC3-HIDDEN','PC3 Hidden Student','pc3-hidden-device','Approved',now(),'Completed','Submitted',0,false,
   'PublicCloud',now(),now()),
  ('a3500000-0000-0000-0000-000000000003','a3100000-0000-0000-0000-000000000001',
   'a3400000-0000-0000-0000-000000000003','a3000000-0000-0000-0000-000000000002',
   'PC3-SHOW','PC3 Show Student','pc3-legacy-device','Approved',now(),'Completed','Submitted',0,false,
   'PublicCloud',now(),now()),
  ('a3500000-0000-0000-0000-000000000004','a3100000-0000-0000-0000-000000000001',
   'a3400000-0000-0000-0000-000000000004','a3000000-0000-0000-0000-000000000002',
   'PC3-SHOW','PC3 Show Student','pc3-corrupt-device','Approved',now(),'Completed','Submitted',0,false,
   'PublicCloud',now(),now());
insert into public.quiz_questions(
  id,organization_id,exam_id,version,sort_order,question_text,points,multiple,created_at,updated_at)
values
  ('a3600000-0000-0000-0000-000000000001','a3100000-0000-0000-0000-000000000001',
   'a3300000-0000-0000-0000-000000000001',1,1,'PC3 weighted 7.5',7.50,false,now(),now()),
  ('a3600000-0000-0000-0000-000000000002','a3100000-0000-0000-0000-000000000001',
   'a3300000-0000-0000-0000-000000000001',1,2,'PC3 weighted 2.5',2.50,false,now(),now()),
  ('a3600000-0000-0000-0000-000000000003','a3100000-0000-0000-0000-000000000001',
   'a3300000-0000-0000-0000-000000000002',1,1,'PC3 hidden 10',10.00,false,now(),now());
insert into public.quiz_choices(
  id,organization_id,question_id,sort_order,choice_text,is_correct,created_at,updated_at)
values
  ('a3700000-0000-4000-8000-000000000001','a3100000-0000-0000-0000-000000000001','a3600000-0000-0000-0000-000000000001',1,'Correct 7.5',true,now(),now()),
  ('a3700000-0000-4000-8000-000000000002','a3100000-0000-0000-0000-000000000001','a3600000-0000-0000-0000-000000000001',2,'Wrong 7.5',false,now(),now()),
  ('a3700000-0000-4000-8000-000000000003','a3100000-0000-0000-0000-000000000001','a3600000-0000-0000-0000-000000000002',1,'Correct 2.5',true,now(),now()),
  ('a3700000-0000-4000-8000-000000000004','a3100000-0000-0000-0000-000000000001','a3600000-0000-0000-0000-000000000002',2,'Wrong 2.5',false,now(),now()),
  ('a3700000-0000-4000-8000-000000000005','a3100000-0000-0000-0000-000000000001','a3600000-0000-0000-0000-000000000003',1,'Correct 10',true,now(),now()),
  ('a3700000-0000-4000-8000-000000000006','a3100000-0000-0000-0000-000000000001','a3600000-0000-0000-0000-000000000003',2,'Wrong 10',false,now(),now());

insert into public.quiz_attempts(
  id,organization_id,session_id,participant_id,attempt_number,exam_version,result_policy,status,
  started_at,deadline_at,finalized_at,auto_score,score,max_score,grading_status,graded_at,
  returned_at,snapshot_json,source_mode,created_at,updated_at)
values
  ('a3800000-0000-0000-0000-000000000001','a3100000-0000-0000-0000-000000000001',
   'a3400000-0000-0000-0000-000000000001','a3500000-0000-0000-0000-000000000001',1,1,
   'ShowAfterSubmission','InProgress','2026-08-07 00:10+00','2026-08-07 01:10+00',null,null,null,10,
   'InProgress',null,null,
   '[{"id":"a3600000-0000-0000-0000-000000000001","sortOrder":1,"questionText":"PC3 weighted 7.5","points":7.50,"multiple":false,"choices":[{"id":"a3700000-0000-4000-8000-000000000001","sortOrder":1,"choiceText":"Correct 7.5"},{"id":"a3700000-0000-4000-8000-000000000002","sortOrder":2,"choiceText":"Wrong 7.5"}]},{"id":"a3600000-0000-0000-0000-000000000002","sortOrder":2,"questionText":"PC3 weighted 2.5","points":2.50,"multiple":false,"choices":[{"id":"a3700000-0000-4000-8000-000000000003","sortOrder":1,"choiceText":"Correct 2.5"},{"id":"a3700000-0000-4000-8000-000000000004","sortOrder":2,"choiceText":"Wrong 2.5"}]}]'::jsonb,
   'PublicCloud',now(),now()),
  ('a3800000-0000-0000-0000-000000000002','a3100000-0000-0000-0000-000000000001',
   'a3400000-0000-0000-0000-000000000002','a3500000-0000-0000-0000-000000000002',1,1,
   'Hidden','InProgress','2026-08-07 00:20+00','2026-08-07 01:20+00',null,null,null,10,
   'InProgress',null,null,
   '[{"id":"a3600000-0000-0000-0000-000000000003","sortOrder":1,"questionText":"PC3 hidden 10","points":10.00,"multiple":false,"choices":[{"id":"a3700000-0000-4000-8000-000000000005","sortOrder":1,"choiceText":"Correct 10"},{"id":"a3700000-0000-4000-8000-000000000006","sortOrder":2,"choiceText":"Wrong 10"}]}]'::jsonb,
   'PublicCloud',now(),now()),
  ('a3800000-0000-0000-0000-000000000003','a3100000-0000-0000-0000-000000000001',
   'a3400000-0000-0000-0000-000000000003','a3500000-0000-0000-0000-000000000003',1,1,
   'ShowAfterSubmission','Finalized','2026-08-06 22:00+00','2026-08-06 23:00+00','2026-08-06 22:30+00',
   7.50,7.50,10,'Graded','2026-08-06 22:30+00',null,
   '[{"id":"a3600000-0000-0000-0000-000000000001","sortOrder":1,"questionText":"PC3 weighted 7.5","points":7.50,"multiple":false,"choices":[{"id":"a3700000-0000-4000-8000-000000000001","sortOrder":1,"choiceText":"Correct 7.5"},{"id":"a3700000-0000-4000-8000-000000000002","sortOrder":2,"choiceText":"Wrong 7.5"}]},{"id":"a3600000-0000-0000-0000-000000000002","sortOrder":2,"questionText":"PC3 weighted 2.5","points":2.50,"multiple":false,"choices":[{"id":"a3700000-0000-4000-8000-000000000003","sortOrder":1,"choiceText":"Correct 2.5"},{"id":"a3700000-0000-4000-8000-000000000004","sortOrder":2,"choiceText":"Wrong 2.5"}]}]'::jsonb,
   'PublicCloud',now(),now());
insert into public.quiz_answers(
  id,organization_id,attempt_id,question_id,choice_ids,revision,client_updated_at,
  source_mode,created_at,updated_at)
values
  ('a3900000-0000-0000-0000-000000000001','a3100000-0000-0000-0000-000000000001','a3800000-0000-0000-0000-000000000001','a3600000-0000-0000-0000-000000000001','["a3700000-0000-4000-8000-000000000001"]',1,now(),'PublicCloud',now(),now()),
  ('a3900000-0000-0000-0000-000000000002','a3100000-0000-0000-0000-000000000001','a3800000-0000-0000-0000-000000000001','a3600000-0000-0000-0000-000000000002','["a3700000-0000-4000-8000-000000000004"]',1,now(),'PublicCloud',now(),now()),
  ('a3900000-0000-0000-0000-000000000003','a3100000-0000-0000-0000-000000000001','a3800000-0000-0000-0000-000000000002','a3600000-0000-0000-0000-000000000003','["a3700000-0000-4000-8000-000000000005"]',1,now(),'PublicCloud',now(),now()),
  ('a3900000-0000-0000-0000-000000000004','a3100000-0000-0000-0000-000000000001','a3800000-0000-0000-0000-000000000003','a3600000-0000-0000-0000-000000000001','["a3700000-0000-4000-8000-000000000001"]',1,now(),'PublicCloud',now(),now()),
  ('a3900000-0000-0000-0000-000000000005','a3100000-0000-0000-0000-000000000001','a3800000-0000-0000-0000-000000000003','a3600000-0000-0000-0000-000000000002','["a3700000-0000-4000-8000-000000000004"]',1,now(),'PublicCloud',now(),now());

create temporary table pc3_state(
  key text primary key,
  payload jsonb,
  stamp timestamptz,
  version bigint) on commit drop;
grant select,insert,update on pc3_state to authenticated;

set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"a3000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
insert into pc3_state(key,payload) values ('show_finalize',public.finalize_public_quiz_attempt(
  'a3800000-0000-0000-0000-000000000001','pc3-show-finalize'));
reset role;
update pc3_state set stamp=(select finalized_at from public.quiz_attempts
  where id='a3800000-0000-0000-0000-000000000001') where key='show_finalize';

select is((select payload->>'status' from pc3_state where key='show_finalize'),'Finalized',
  'ShowAfterSubmission finalize returns Finalized status');
select ok((select status='Finalized' and grading_status='Graded' and returned_at is null
  from public.quiz_attempts where id='a3800000-0000-0000-0000-000000000001'),
  'auto publication preserves Graded state without a manual return timestamp');
select is((select score from public.quiz_attempts
  where id='a3800000-0000-0000-0000-000000000001'),7.50::numeric,
  'finalize persists weighted authoritative score');
select is((select payload->>'scoreVisible' from pc3_state where key='show_finalize'),'true',
  'ShowAfterSubmission finalize publishes score immediately');
select is(((select payload from pc3_state where key='show_finalize')->>'score')::numeric,7.50::numeric,
  'finalize response uses persisted authoritative score');
select is(((select payload from pc3_state where key='show_finalize')->>'maxScore')::numeric,10.00::numeric,
  'finalize response uses authoritative max score');

set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"a3000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
select is(public.finalize_public_quiz_attempt(
  'a3800000-0000-0000-0000-000000000001','pc3-show-finalize'),
  (select payload from pc3_state where key='show_finalize'),
  'finalize retry returns the same authoritative projection');
reset role;
select is((select finalized_at from public.quiz_attempts
  where id='a3800000-0000-0000-0000-000000000001'),
  (select stamp from pc3_state where key='show_finalize'),
  'finalize retry preserves finalized_at');

set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"a3000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
insert into pc3_state(key,payload) values ('show_review',public.get_public_quiz_attempt_review(
  'a3800000-0000-0000-0000-000000000001'));
select is((select payload->>'scoreVisible' from pc3_state where key='show_review'),'true',
  'auto-published review exposes score');
select is(((select payload from pc3_state where key='show_review')->>'score')::numeric,7.50::numeric,
  'auto-published review uses authoritative score');
select is((select payload->>'correctAnswersVisible' from pc3_state where key='show_review'),'false',
  'auto-published review keeps correct answers hidden');
select ok(not exists(
    select 1
    from pg_catalog.jsonb_array_elements((select payload->'questions' from pc3_state where key='show_review')) q,
         lateral pg_catalog.jsonb_array_elements(q->'choices') c
    where c ? 'correct')
  and position('answerKey' in (select payload::text from pc3_state where key='show_review'))=0
  and position('correctChoiceIds' in (select payload::text from pc3_state where key='show_review'))=0,
  'auto-published review exposes no answer key field');
insert into pc3_state(key,payload) values ('show_history',public.get_student_results());
select is(pg_catalog.jsonb_array_length((select payload->'items' from pc3_state where key='show_history')),2,
  'current and legacy ShowAfterSubmission attempts appear in history');
select ok((select item->>'returnedAtUtc'=item->>'finalizedAtUtc'
    and item->>'startedAtUtc'='2026-08-07T00:10:00+00:00'
    and (item->>'durationSeconds')::bigint =
      floor(extract(epoch from ((item->>'finalizedAtUtc')::timestamptz-(item->>'startedAtUtc')::timestamptz)))::bigint
  from pg_catalog.jsonb_array_elements((select payload->'items' from pc3_state where key='show_history')) item
  where item->>'attemptId'='a3800000-0000-0000-0000-000000000001'),
  'auto publication maps server start, finalize, publication and duration timestamps');
select ok(exists(select 1
  from pg_catalog.jsonb_array_elements((select payload->'items' from pc3_state where key='show_history')) item
  where item->>'attemptId'='a3800000-0000-0000-0000-000000000003'
    and (item->>'score')::numeric=7.50
    and item->>'returnedAtUtc'=item->>'finalizedAtUtc'),
  'legacy finalized Graded ShowAfterSubmission attempt is published without state backfill');
select set_config('request.jwt.claims',
  '{"sub":"a3000000-0000-0000-0000-000000000004","role":"authenticated"}',true);
select throws_ok($$select public.get_public_quiz_attempt(
  'a3800000-0000-0000-0000-000000000001')$$,
  'P0002','PUBLIC_QUIZ_ATTEMPT_NOT_FOUND','peer cannot read another student attempt');
select set_config('request.jwt.claims',
  '{"sub":"a3000000-0000-0000-0000-000000000005","role":"authenticated"}',true);
select throws_ok($$select public.get_public_quiz_attempt(
  'a3800000-0000-0000-0000-000000000001')$$,
  'P0002','PUBLIC_QUIZ_ATTEMPT_NOT_FOUND','other tenant cannot read the attempt');

select set_config('request.jwt.claims',
  '{"sub":"a3000000-0000-0000-0000-000000000003","role":"authenticated"}',true);
insert into pc3_state(key,payload) values ('hidden_finalize',public.finalize_public_quiz_attempt(
  'a3800000-0000-0000-0000-000000000002','pc3-hidden-finalize'));
reset role;
select is((select score from public.quiz_attempts
  where id='a3800000-0000-0000-0000-000000000002'),10.00::numeric,
  'Hidden finalize still persists authoritative score');
select ok((select payload->>'scoreVisible'='false' and payload->'score'='null'::jsonb
  from pc3_state where key='hidden_finalize'),
  'Hidden finalize response masks score');
set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"a3000000-0000-0000-0000-000000000003","role":"authenticated"}',true);
select ok((select payload->>'scoreVisible'='false' and payload->'score'='null'::jsonb
  from (select public.get_public_quiz_attempt_review(
    'a3800000-0000-0000-0000-000000000002') payload) review),
  'Hidden review masks score before manual Return');
select is(public.get_student_results()->'items','[]'::jsonb,
  'Hidden attempt is absent from history before manual Return');
reset role;
update pc3_state set version=(select cloud_version from public.quiz_attempts
  where id='a3800000-0000-0000-0000-000000000002') where key='hidden_finalize';

set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"a3000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
insert into pc3_state(key,payload) values ('hidden_return',public.return_public_quiz_grade(
  'a3800000-0000-0000-0000-000000000002','PC3 returned',
  (select version from pc3_state where key='hidden_finalize'),
  'a3f00000-0000-0000-0000-000000000001'));
select ok((select payload->>'gradingStatus'='Returned' and (payload->>'score')::numeric=10
  from pc3_state where key='hidden_return'),
  'manual Return publishes the existing authoritative score');
reset role;
update pc3_state set version=(select cloud_version from public.quiz_attempts
  where id='a3800000-0000-0000-0000-000000000002') where key='hidden_return';

set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"a3000000-0000-0000-0000-000000000003","role":"authenticated"}',true);
select ok((select result->>'scoreVisible'='true' and (result->>'score')::numeric=10
  from (select public.get_public_quiz_attempt(
    'a3800000-0000-0000-0000-000000000002') result) attempt),
  'Hidden attempt becomes visible after manual Return');
select is(pg_catalog.jsonb_array_length(public.get_student_results()->'items'),1,
  'manually Returned Hidden attempt appears in history');
select is(public.get_public_quiz_attempt_review(
  'a3800000-0000-0000-0000-000000000002')->>'correctAnswersVisible','true',
  'manual Return preserves existing answer-key policy');

select set_config('request.jwt.claims',
  '{"sub":"a3000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
insert into pc3_state(key,payload) values ('hidden_reopen',public.reopen_public_quiz_grade(
  'a3800000-0000-0000-0000-000000000002','PC3 recheck',
  (select version from pc3_state where key='hidden_return'),
  'a3f00000-0000-0000-0000-000000000002'));
select is((select payload->>'gradingStatus' from pc3_state where key='hidden_reopen'),'Graded',
  'Reopen restores Graded state');
select set_config('request.jwt.claims',
  '{"sub":"a3000000-0000-0000-0000-000000000003","role":"authenticated"}',true);
select is(public.get_student_results()->'items','[]'::jsonb,
  'Reopen removes Hidden attempt from student history');
select is(public.get_public_quiz_attempt_review(
  'a3800000-0000-0000-0000-000000000002')->>'scoreVisible','false',
  'Reopen masks Hidden review score again');

reset role;
insert into public.quiz_attempts(
  id,organization_id,session_id,participant_id,attempt_number,exam_version,result_policy,status,
  started_at,deadline_at,finalized_at,auto_score,score,max_score,grading_status,graded_at,
  returned_at,snapshot_json,source_mode,created_at,updated_at)
values ('a3800000-0000-0000-0000-000000000004','a3100000-0000-0000-0000-000000000001',
  'a3400000-0000-0000-0000-000000000004','a3500000-0000-0000-0000-000000000004',1,1,
  'ShowAfterSubmission','Finalized','2026-08-06 20:00+00','2026-08-06 21:00+00','2026-08-06 20:30+00',
  9,9,10,'Graded','2026-08-06 20:30+00',null,
  '[{"id":"a3600000-0000-0000-0000-000000000001","sortOrder":1,"questionText":"PC3 weighted 7.5","points":7.50,"multiple":false,"choices":[{"id":"a3700000-0000-4000-8000-000000000001","sortOrder":1,"choiceText":"Correct 7.5"},{"id":"a3700000-0000-4000-8000-000000000002","sortOrder":2,"choiceText":"Wrong 7.5"}]},{"id":"a3600000-0000-0000-0000-000000000002","sortOrder":2,"questionText":"PC3 weighted 2.5","points":2.50,"multiple":false,"choices":[{"id":"a3700000-0000-4000-8000-000000000003","sortOrder":1,"choiceText":"Correct 2.5"},{"id":"a3700000-0000-4000-8000-000000000004","sortOrder":2,"choiceText":"Wrong 2.5"}]}]'::jsonb,
  'PublicCloud',now(),now());
insert into public.quiz_answers(
  id,organization_id,attempt_id,question_id,choice_ids,revision,client_updated_at,
  source_mode,created_at,updated_at)
values
  ('a3900000-0000-0000-0000-000000000006','a3100000-0000-0000-0000-000000000001','a3800000-0000-0000-0000-000000000004','a3600000-0000-0000-0000-000000000001','["a3700000-0000-4000-8000-000000000001"]',1,now(),'PublicCloud',now(),now()),
  ('a3900000-0000-0000-0000-000000000007','a3100000-0000-0000-0000-000000000001','a3800000-0000-0000-0000-000000000004','a3600000-0000-0000-0000-000000000002','["a3700000-0000-4000-8000-000000000004"]',1,now(),'PublicCloud',now(),now());
set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"a3000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
select throws_ok($$select public.get_public_quiz_attempt(
  'a3800000-0000-0000-0000-000000000004')$$,
  '22023','QUIZ_GRADE_NOT_AUTHORITATIVE','inconsistent persisted score fails closed in attempt projection');
select throws_ok($$select public.get_public_quiz_attempt_review(
  'a3800000-0000-0000-0000-000000000004')$$,
  '22023','QUIZ_GRADE_NOT_AUTHORITATIVE','inconsistent persisted score fails closed in review');
select ok(not (public.get_student_results()->'items' @>
  '[{"attemptId":"a3800000-0000-0000-0000-000000000004"}]'::jsonb),
  'inconsistent persisted score is not auto-published in history');
reset role;
update public.quiz_attempts set snapshot_json='[]'::jsonb
where id='a3800000-0000-0000-0000-000000000004';
set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"a3000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
select throws_ok($$select public.get_public_quiz_attempt(
  'a3800000-0000-0000-0000-000000000004')$$,
  '55000','QUIZ_ATTEMPT_SNAPSHOT_INVALID','invalid snapshot remains fail closed');

select * from finish();
rollback;
