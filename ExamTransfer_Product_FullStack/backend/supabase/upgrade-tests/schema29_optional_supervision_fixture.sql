do $fixture_precondition$
begin
  if (select max(version) from supabase_migrations.schema_migrations)
       <> '20260805133110'
     or (select schema_version from public.examtransfer_cloud_meta where id=1)
       <> 29 then
    raise exception 'SCHEMA29_FIXTURE_WRONG_MIGRATION_TARGET';
  end if;
end
$fixture_precondition$;

insert into auth.users(id,email) values
  ('92000000-0000-0000-0000-000000000001','schema29-teacher@example.test')
on conflict (id) do nothing;
insert into public.organizations(id,name) values
  ('92100000-0000-0000-0000-000000000001','Schema 29 Quiz Runtime Org');
insert into public.profiles(
  id,organization_id,display_name,role,username,is_active)
values (
  '92000000-0000-0000-0000-000000000001',
  '92100000-0000-0000-0000-000000000001',
  'Schema 29 Teacher','Teacher','schema29-teacher',true);
insert into public.classes(
  id,organization_id,name,code,school_year,status,access_mode,created_by,created_at,updated_at)
values (
  '92200000-0000-0000-0000-000000000001',
  '92100000-0000-0000-0000-000000000001',
  'Schema 29 Class','S29','2026','Active','Public',
  '92000000-0000-0000-0000-000000000001',now(),now());
insert into public.exams(
  id,organization_id,class_id,title,subject,duration_minutes,status,version,created_by,
  delivery_type,quiz_result_policy,supervision_mode,created_at,updated_at)
values (
  '92300000-0000-0000-0000-000000000001',
  '92100000-0000-0000-0000-000000000001',
  '92200000-0000-0000-0000-000000000001',
  'Schema 29 Quiz','Test',30,'Published',1,
  '92000000-0000-0000-0000-000000000001',
  'MultipleChoice','Hidden','Standard',now(),now());
insert into public.quiz_questions(
  id,organization_id,exam_id,version,sort_order,question_text,points,multiple)
values (
  '92400000-0000-0000-0000-000000000001',
  '92100000-0000-0000-0000-000000000001',
  '92300000-0000-0000-0000-000000000001',1,1,
  'Schema 29 preserved question',10,false);
insert into public.quiz_choices(
  id,organization_id,question_id,sort_order,choice_text,is_correct)
values
  ('92500000-0000-4000-8000-000000000001',
   '92100000-0000-0000-0000-000000000001',
   '92400000-0000-0000-0000-000000000001',1,'Preserved A',true),
  ('92500000-0000-4000-8000-000000000002',
   '92100000-0000-0000-0000-000000000001',
   '92400000-0000-0000-0000-000000000001',2,'Preserved B',false);

do $fixture_loaded$
begin
  if (select count(*) from public.quiz_questions
      where exam_id='92300000-0000-0000-0000-000000000001') <> 1
     or (select count(*) from public.quiz_choices
         where question_id='92400000-0000-0000-0000-000000000001') <> 2 then
    raise exception 'SCHEMA29_QUIZ_FIXTURE_LOAD_FAILED';
  end if;
end
$fixture_loaded$;
