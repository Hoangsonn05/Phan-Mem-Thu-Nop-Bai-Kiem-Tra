-- Seeded after reset to 202607220001, before PublicCloud ownership columns.
do $fixture_precondition$
begin
  if (select max(version) from supabase_migrations.schema_migrations) <> '202607220001' then
    raise exception 'LEGACY_FIXTURE_WRONG_MIGRATION_TARGET';
  end if;
  if to_regclass('public.submission_files') is null
     or exists (
       select 1
       from information_schema.columns
       where table_schema='public'
         and table_name='submission_files'
         and column_name='source_mode'
     ) then
    raise exception 'LEGACY_FIXTURE_SCHEMA_PRECONDITION_FAILED';
  end if;
end
$fixture_precondition$;

insert into auth.users(id,email)
values
  ('71000000-0000-0000-0000-000000000001','legacy-student@example.test'),
  ('71000000-0000-0000-0000-000000000002','legacy-teacher@example.test')
on conflict (id) do nothing;

insert into public.organizations(id,name,created_at,updated_at)
values ('71000000-0000-0000-0000-000000000000','Legacy Upgrade Org',now(),now());

insert into public.profiles(
  id,organization_id,display_name,role,username,student_code,is_active,date_of_birth,created_at,updated_at)
values (
  '71000000-0000-0000-0000-000000000001',
  '71000000-0000-0000-0000-000000000000',
  'Legacy Student','Student','LEGACY01','LEGACY01',true,'2008-01-01',now(),now());

insert into public.profiles(
  id,organization_id,display_name,role,username,student_code,is_active,date_of_birth,created_at,updated_at)
values (
  '71000000-0000-0000-0000-000000000002',
  '71000000-0000-0000-0000-000000000000',
  'Legacy Teacher','Teacher','LEGACY-TEACHER',null,true,null,now(),now());

insert into public.classes(id,organization_id,name,code,school_year,status,created_at,updated_at)
values (
  '71100000-0000-0000-0000-000000000000',
  '71000000-0000-0000-0000-000000000000',
  'Legacy Class','LEGACY','2026','Active',now(),now());

insert into public.class_members(
  id,organization_id,class_id,user_id,student_code,display_name,email,metadata_json,created_at,updated_at)
values (
  '71100000-0000-0000-0000-000000000001',
  '71000000-0000-0000-0000-000000000000',
  '71100000-0000-0000-0000-000000000000',
  '71000000-0000-0000-0000-000000000001',
  'LEGACY01','Legacy Student','legacy-student@example.test','{"origin":"legacy-lan"}',now(),now());

insert into public.exams(
  id,organization_id,class_id,title,subject,duration_minutes,status,version,created_at,updated_at)
values (
  '71200000-0000-0000-0000-000000000000',
  '71000000-0000-0000-0000-000000000000',
  '71100000-0000-0000-0000-000000000000',
  'Legacy Exam','IT',60,'Published',1,now(),now());

insert into public.exam_sessions(
  id,organization_id,exam_id,class_id,room_code,status,started_at,created_at,updated_at)
values (
  '71300000-0000-0000-0000-000000000000',
  '71000000-0000-0000-0000-000000000000',
  '71200000-0000-0000-0000-000000000000',
  '71100000-0000-0000-0000-000000000000',
  'LEGACY','Finished',now()-interval '2 hours',now(),now());

insert into public.session_participants(
  id,organization_id,session_id,user_id,student_code,display_name,device_id,status,
  joined_at,download_status,submission_status,extra_time_minutes,resubmit_allowed,created_at,updated_at)
values (
  '71400000-0000-0000-0000-000000000000',
  '71000000-0000-0000-0000-000000000000',
  '71300000-0000-0000-0000-000000000000',
  '71000000-0000-0000-0000-000000000001',
  'LEGACY01','Legacy Student','legacy-device','Approved',now()-interval '2 hours',
  'Completed','Submitted',0,false,now(),now());

insert into public.submissions(
  id,organization_id,session_id,participant_id,attempt_number,status,
  deadline_at,is_late,is_official,idempotency_key,created_at,updated_at)
values (
  '71500000-0000-0000-0000-000000000000',
  '71000000-0000-0000-0000-000000000000',
  '71300000-0000-0000-0000-000000000000',
  '71400000-0000-0000-0000-000000000000',
  1,'Submitted',now()-interval '1 hour',false,true,'legacy-idempotency',now(),now());

insert into public.submission_files(
  id,organization_id,submission_id,client_file_id,name,stored_name,mime_type,size_bytes,
  sha256,transfer_status,sync_status,cloud_object_path,created_at,updated_at)
values
  ('71600000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000000',
   '71500000-0000-0000-0000-000000000000','legacy-1','part-one.txt','part-one.txt',
   'text/plain',4,repeat('a',64),'Completed','Synced',null,now(),now()),
  ('71600000-0000-0000-0000-000000000002','71000000-0000-0000-0000-000000000000',
   '71500000-0000-0000-0000-000000000000','legacy-2','part-two.txt','part-two.txt',
   'text/plain',4,repeat('b',64),'Completed','Synced',null,now(),now());

do $fixture_loaded$
begin
  if (select count(*) from public.profiles
      where id in (
        '71000000-0000-0000-0000-000000000001',
        '71000000-0000-0000-0000-000000000002')) <> 2
     or (select count(*) from public.class_members
         where id='71100000-0000-0000-0000-000000000001') <> 1
     or (select count(*) from public.submission_files
         where submission_id='71500000-0000-0000-0000-000000000000') <> 2 then
    raise exception 'LEGACY_FIXTURE_LOAD_VERIFICATION_FAILED';
  end if;
end
$fixture_loaded$;
