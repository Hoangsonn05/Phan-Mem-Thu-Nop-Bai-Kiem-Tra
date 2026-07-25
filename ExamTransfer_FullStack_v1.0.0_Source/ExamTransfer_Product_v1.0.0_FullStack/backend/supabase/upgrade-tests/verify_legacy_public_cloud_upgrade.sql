begin;
select plan(26);

select is(
  (select count(*)::integer from public.organizations
   where id='71000000-0000-0000-0000-000000000000'),
  1,
  'legacy organization survives the PublicCloud migration');

select is(
  (select role from public.profiles
   where id='71000000-0000-0000-0000-000000000002'),
  'Teacher',
  'legacy teacher profile survives the PublicCloud migration');

select is(
  (select role from public.profiles
   where id='71000000-0000-0000-0000-000000000001'),
  'Student',
  'legacy student profile survives the PublicCloud migration');

select is(
  (select source_mode from public.class_members
   where id='71100000-0000-0000-0000-000000000001'),
  'Lan',
  'legacy class membership remains LAN-owned');

select is(
  (select access_mode from public.exam_sessions
   where id='71300000-0000-0000-0000-000000000000'),
  'LanOnly',
  'legacy session remains LanOnly');

select is(
  (select source_mode from public.session_participants
   where id='71400000-0000-0000-0000-000000000000'),
  'Lan',
  'legacy participant remains LAN-owned');

select is(
  (select count(*)::integer from public.submission_files
   where submission_id='71500000-0000-0000-0000-000000000000'),
  2,
  'both legacy LAN submission files survive the PublicCloud migration');

select is(
  (select count(*)::integer from public.submission_files
   where submission_id='71500000-0000-0000-0000-000000000000' and source_mode='Lan'),
  2,
  'legacy files are classified as Lan');

select is(
  (select source_mode from public.submissions
   where id='71500000-0000-0000-0000-000000000000'),
  'Lan',
  'legacy submission remains Lan-owned');

select ok(
  not exists (
    select 1
    from pg_index i
    join pg_class idx on idx.oid=i.indexrelid
    join pg_class tab on tab.oid=i.indrelid
    join pg_namespace n on n.oid=tab.relnamespace
    where n.nspname='public'
      and tab.relname='submission_files'
      and i.indisunique
      and i.indpred is null
      and pg_get_indexdef(idx.oid) like '%(submission_id)%'
  ),
  'no global unique submission_files(submission_id) index exists');

select ok(
  (select pg_get_expr(i.indpred, i.indrelid) like '%PublicCloud%'
   from pg_index i
   join pg_class c on c.oid=i.indexrelid
   where c.relname='ux_public_submission_single_file'),
  'single-file uniqueness is partial to PublicCloud');

select lives_ok(
  $$insert into public.submission_files(
      id,organization_id,submission_id,client_file_id,name,stored_name,mime_type,size_bytes,
      sha256,transfer_status,sync_status,cloud_object_path,source_mode,created_at,updated_at)
    values (
      '71600000-0000-0000-0000-000000000003','71000000-0000-0000-0000-000000000000',
      '71500000-0000-0000-0000-000000000000','legacy-3','oversized-plain.txt','oversized-plain.txt',
      'text/plain',12582912,repeat('c',64),'Completed','Synced',null,'Lan',now(),now())$$,
  '10 MiB/archive trigger does not block valid legacy LAN data');

select ok(
  pg_get_functiondef('public.enforce_student_submission_policy()'::regprocedure)
    like '%source_mode <> ''PublicCloud''%'
  and pg_get_functiondef('public.enforce_student_submission_policy()'::regprocedure)
    like '%10485760%',
  'submission trigger limits are scoped to PublicCloud');

select is(
  (select schema_version from public.examtransfer_cloud_meta where id=1),
  15,
  'upgrade reaches schema compatibility version 15');

select has_column('public', 'submissions', 'cloud_version',
  'cloud_version exists after upgrade');

select is(
  (select cloud_version from public.submissions
   where id='71500000-0000-0000-0000-000000000000'),
  0::bigint,
  'legacy LAN submission is not assigned a PublicCloud version');

select has_table('public', 'cloud_sync_cursors',
  'cloud cursor table exists after upgrade');

select has_column('public', 'cloud_sync_cursors', 'entity_name',
  'cloud cursor is scoped per entity');

select has_column('public', 'cloud_sync_cursors', 'last_updated_at',
  'cloud cursor stores the updated_at tie breaker');

select has_column('public', 'cloud_sync_cursors', 'last_id',
  'cloud cursor stores the id tie breaker');

select is(
  (select count(*)::integer
   from pg_proc p
   join pg_namespace n on n.oid=p.pronamespace
   where n.nspname='public'
     and p.proname in (
       'approve_public_participant',
       'reject_public_participant',
       'bulk_approve_public_participants',
       'add_public_participant_extra_time',
       'allow_public_resubmission',
       'reject_public_submission',
       'approve_public_enrollment_request',
       'reject_public_enrollment_request')),
  8,
  'all teacher mutation RPCs exist after upgrade');

select ok(
  (select relrowsecurity from pg_class
   where oid='public.submissions'::regclass)
  and
  (select relrowsecurity from pg_class
   where oid='public.class_members'::regclass),
  'RLS remains enabled on upgraded tenant tables');

select ok(
  (select count(*) from pg_policies
   where schemaname='public'
     and tablename in ('submissions','class_members')) > 0,
  'RLS policies remain present after upgrade');

select ok(
  (select not public and file_size_limit=10485760
   from storage.buckets
   where id='public-submission-archives'),
  'PublicCloud submission bucket is private and capped at 10 MiB');

select is(
  (select count(*)::integer
   from public.submission_files f
   left join public.submissions s on s.id=f.submission_id
   left join public.session_participants p on p.id=s.participant_id
   left join public.exam_sessions es on es.id=s.session_id
   where f.submission_id='71500000-0000-0000-0000-000000000000'
     and (s.id is null or p.id is null or es.id is null)),
  0,
  'upgrade creates no orphan in the legacy submission chain');

select is(
  (select count(*)::integer from public.class_members
   where class_id='71100000-0000-0000-0000-000000000000'
     and student_code='LEGACY01'),
  1,
  'upgrade creates no duplicate legacy class membership');

select * from finish();
rollback;
