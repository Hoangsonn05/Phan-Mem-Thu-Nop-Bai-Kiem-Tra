begin;
create extension if not exists pgtap with schema extensions;
set local search_path = public, extensions;
select plan(19);

-- 1. Schema version includes the PublicCloud quiz runtime gate.
select is(
  (select schema_version from public.examtransfer_cloud_meta where id=1),33,
  'schema version is 33 after the teacher quiz replica pull fix');

-- 2. The active function carries the correct 'Completed' assignment.
select ok(
  position('transfer_status = ''Completed''' in
    pg_catalog.pg_get_functiondef(
      pg_catalog.to_regprocedure(
        'public.verify_public_submission_archive(uuid,uuid,text,bigint,text)'))) > 0,
  'active function writes Completed on success');

-- 3. The active function no longer carries the incorrect 'Verified' assignment.
select ok(
  position('transfer_status = ''Verified''' in
    pg_catalog.pg_get_functiondef(
      pg_catalog.to_regprocedure(
        'public.verify_public_submission_archive(uuid,uuid,text,bigint,text)'))) = 0,
  'active function does not write Verified');

-- 4. Security definer is retained.
select ok(
  (select prosecdef from pg_catalog.pg_proc where oid =
    pg_catalog.to_regprocedure(
      'public.verify_public_submission_archive(uuid,uuid,text,bigint,text)')),
  'function retains SECURITY DEFINER');

-- 5. Empty search_path is retained.
select ok(
  'search_path=""' = any(coalesce(
    (select proconfig from pg_catalog.pg_proc where oid =
      pg_catalog.to_regprocedure(
        'public.verify_public_submission_archive(uuid,uuid,text,bigint,text)')),
    array[]::text[])),
  'function retains SET search_path = ''''');

-- 6. service_role can EXECUTE the function; authenticated cannot.
select ok(
  has_function_privilege('service_role',
    'public.verify_public_submission_archive(uuid,uuid,text,bigint,text)', 'EXECUTE'),
  'service_role retains EXECUTE grant');
select ok(
  not has_function_privilege('authenticated',
    'public.verify_public_submission_archive(uuid,uuid,text,bigint,text)', 'EXECUTE'),
  'authenticated has no EXECUTE grant');

-- -----------------------------------------------------------------------
-- Behavioural tests: seed data, invoke function, verify outcomes.
-- All test data uses a dedicated namespace prefix (pc28) and is rolled back.
-- -----------------------------------------------------------------------

-- Seed: organisation, auth users, profiles.
insert into auth.users(id, email) values
  ('b0000028-0000-0000-0000-000000000001', 'pc28-teacher@example.test'),
  ('b0000028-0000-0000-0000-000000000002', 'pc28-student@example.test')
on conflict (id) do nothing;

insert into public.organizations(id, name) values
  ('b0100028-0000-0000-0000-000000000001', 'PC28 Test Org')
on conflict (id) do nothing;

insert into public.profiles(
  id, organization_id, display_name, role, username, student_code, is_active, date_of_birth)
values
  ('b0000028-0000-0000-0000-000000000001', 'b0100028-0000-0000-0000-000000000001',
   'PC28 Teacher', 'Teacher', 'pc28-teacher', null, true, null),
  ('b0000028-0000-0000-0000-000000000002', 'b0100028-0000-0000-0000-000000000001',
   'PC28 Student', 'Student', 'PC28-S1', 'PC28-S1', true, '2008-01-01')
on conflict (id) do nothing;

-- Seed: class, exam, session.
insert into public.classes(
  id, organization_id, name, code, school_year, status, access_mode, created_by, created_at, updated_at)
values ('b0200028-0000-0000-0000-000000000001', 'b0100028-0000-0000-0000-000000000001',
        'PC28 Class', 'PC28CLS', '2026', 'Active', 'Public',
        'b0000028-0000-0000-0000-000000000001', now(), now());

insert into public.exams(
  id, organization_id, class_id, title, subject, duration_minutes, status, version, created_by,
  delivery_type, quiz_result_policy, supervision_mode, created_at, updated_at)
values ('b0300028-0000-0000-0000-000000000001', 'b0100028-0000-0000-0000-000000000001',
        'b0200028-0000-0000-0000-000000000001', 'PC28 Essay', 'Test', 60, 'Published', 1,
        'b0000028-0000-0000-0000-000000000001', 'FileSubmission', 'Hidden', 'Standard', now(), now());

insert into public.exam_sessions(
  id, organization_id, exam_id, class_id, room_code, status, started_at, access_mode,
  auto_approve, accepting_participants, delivery_type, supervision_mode, quiz_result_policy,
  exam_version, created_at, updated_at)
values ('b0400028-0000-0000-0000-000000000001', 'b0100028-0000-0000-0000-000000000001',
        'b0300028-0000-0000-0000-000000000001', 'b0200028-0000-0000-0000-000000000001',
        'PC28R1', 'InProgress', now(), 'PublicCloud', false, true,
        'FileSubmission', 'Standard', 'Hidden', 1, now(), now());

-- Seed: participant.
insert into public.session_participants(
  id, organization_id, session_id, user_id, student_code, display_name, device_id,
  status, joined_at, download_status, submission_status, extra_time_minutes,
  resubmit_allowed, source_mode, created_at, updated_at)
values ('b0500028-0000-0000-0000-000000000001', 'b0100028-0000-0000-0000-000000000001',
        'b0400028-0000-0000-0000-000000000001', 'b0000028-0000-0000-0000-000000000002',
        'PC28-S1', 'PC28 Student', 'pc28-dev-1', 'Approved', now(),
        'Completed', 'Uploading', 0, false, 'PublicCloud', now(), now());

-- Seed: submission in 'Uploading' state (the state the function requires).
insert into public.submissions(
  id, organization_id, session_id, participant_id, attempt_number, status, deadline_at,
  is_late, is_official, idempotency_key, source_mode, created_at, updated_at)
values ('b0600028-0000-0000-0000-000000000001', 'b0100028-0000-0000-0000-000000000001',
        'b0400028-0000-0000-0000-000000000001', 'b0500028-0000-0000-0000-000000000001',
        1, 'Uploading', now() + interval '1 hour', false, true,
        'pc28-idempotency-1', 'PublicCloud', now(), now());

-- Each PublicCloud attempt owns exactly one archive. Additional rows used by
-- the failure/backfill assertions therefore use separate submissions.
insert into public.submissions(
  id, organization_id, session_id, participant_id, attempt_number, status, deadline_at,
  is_late, is_official, idempotency_key, source_mode, created_at, updated_at)
values
  ('b0600028-0000-0000-0000-000000000002', 'b0100028-0000-0000-0000-000000000001',
   'b0400028-0000-0000-0000-000000000001', 'b0500028-0000-0000-0000-000000000001',
   2, 'Uploading', now() + interval '1 hour', false, false,
   'pc28-idempotency-2', 'PublicCloud', now(), now()),
  ('b0600028-0000-0000-0000-000000000003', 'b0100028-0000-0000-0000-000000000001',
   'b0400028-0000-0000-0000-000000000001', 'b0500028-0000-0000-0000-000000000001',
   3, 'Uploading', now() + interval '1 hour', false, false,
   'pc28-idempotency-3', 'PublicCloud', now(), now()),
  ('b0600028-0000-0000-0000-000000000004', 'b0100028-0000-0000-0000-000000000001',
   'b0400028-0000-0000-0000-000000000001', 'b0500028-0000-0000-0000-000000000001',
   4, 'Uploading', now() + interval '1 hour', false, false,
   'pc28-idempotency-4', 'PublicCloud', now(), now()),
  ('b0600028-0000-0000-0000-000000000005', 'b0100028-0000-0000-0000-000000000001',
   'b0400028-0000-0000-0000-000000000001', 'b0500028-0000-0000-0000-000000000001',
   5, 'Uploading', now() + interval '1 hour', false, false,
   'pc28-idempotency-5', 'PublicCloud', now(), now()),
  ('b0600028-0000-0000-0000-000000000006', 'b0100028-0000-0000-0000-000000000001',
   'b0400028-0000-0000-0000-000000000001', 'b0500028-0000-0000-0000-000000000001',
   6, 'Uploading', now() + interval '1 hour', false, false,
   'pc28-idempotency-6', 'PublicCloud', now(), now());

-- Seed: submission_file with a known SHA-256 and cloud_object_path.
-- SHA-256 of the string "pc28-archive-content": we use a fixed valid hex string.
insert into public.submission_files(
  id, organization_id, submission_id, name, size_bytes, sha256, mime_type,
  cloud_object_path, transfer_status, sync_status, archive_signature_verified,
  source_mode, created_at, updated_at)
values (
  'b0700028-0000-0000-0000-000000000001',
  'b0100028-0000-0000-0000-000000000001',
  'b0600028-0000-0000-0000-000000000001',
  'submission.zip',
  1024,
  'a3c38d6a1d3b6a8e9f1c2d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4',
  'application/zip',
  'b0100028-0000-0000-0000-000000000001/public-submissions/b0000028-0000-0000-0000-000000000002/b0600028-0000-0000-0000-000000000001/b0700028-0000-0000-0000-000000000001.zip',
  'Queued',
  'Pending',
  false,
  'PublicCloud',
  now(), now());

-- Capture cloud_version before calling the function.
create temporary table pc28_before as
  select cloud_version as v_before from public.submission_files
  where id = 'b0700028-0000-0000-0000-000000000001';

-- 8. Invoke verify_public_submission_archive as service_role.
set local role service_role;
select set_config('request.jwt.claims', '{"role":"service_role"}', true);

select lives_ok($$
  select public.verify_public_submission_archive(
    'b0600028-0000-0000-0000-000000000001'::uuid,
    'b0700028-0000-0000-0000-000000000001'::uuid,
    'a3c38d6a1d3b6a8e9f1c2d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4',
    1024::bigint,
    'zip')
$$, 'verify_public_submission_archive succeeds with correct arguments');

reset role;

-- 9. After successful verification, transfer_status = 'Completed'.
select is(
  (select transfer_status from public.submission_files
   where id = 'b0700028-0000-0000-0000-000000000001'),
  'Completed',
  'successful verification writes Completed, not Verified');

-- 10. archive_signature_verified = true after success.
select ok(
  (select archive_signature_verified from public.submission_files
   where id = 'b0700028-0000-0000-0000-000000000001'),
  'archive_signature_verified is true after successful verification');

-- 11. cloud_version was incremented (must be strictly greater than before).
select ok(
  (select cloud_version from public.submission_files
   where id = 'b0700028-0000-0000-0000-000000000001')
  > (select v_before from pc28_before),
  'cloud_version is incremented after successful verification');

-- 12. Failed verification branch: wrong hash must NOT write Completed.
-- Seed a second file to test the failure path.
insert into public.submission_files(
  id, organization_id, submission_id, name, size_bytes, sha256, mime_type,
  cloud_object_path, transfer_status, sync_status, archive_signature_verified,
  source_mode, created_at, updated_at)
values (
  'b0700028-0000-0000-0000-000000000002',
  'b0100028-0000-0000-0000-000000000001',
  'b0600028-0000-0000-0000-000000000002',
  'submission2.zip',
  512,
  'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff',
  'application/zip',
  'b0100028-0000-0000-0000-000000000001/public-submissions/b0000028-0000-0000-0000-000000000002/b0600028-0000-0000-0000-000000000002/b0700028-0000-0000-0000-000000000002.zip',
  'Queued',
  'Pending',
  false,
  'PublicCloud',
  now(), now());

-- Reset submission to Uploading so it can be found by the function.
update public.submissions set status = 'Uploading'
where id = 'b0600028-0000-0000-0000-000000000002';

set local role service_role;
select set_config('request.jwt.claims', '{"role":"service_role"}', true);

select throws_ok($$
  select public.verify_public_submission_archive(
    'b0600028-0000-0000-0000-000000000002'::uuid,
    'b0700028-0000-0000-0000-000000000002'::uuid,
    '0000000000000000000000000000000000000000000000000000000000000000',
    512::bigint,
    'zip')
$$, 'ARCHIVE_HASH_MISMATCH', 'failed verification raises ARCHIVE_HASH_MISMATCH');

reset role;

select is(
  (select transfer_status from public.submission_files
   where id = 'b0700028-0000-0000-0000-000000000002'),
  'Queued',
  'failed verification does not change transfer_status from Queued');

select ok(
  not (select archive_signature_verified from public.submission_files
       where id = 'b0700028-0000-0000-0000-000000000002'),
  'failed verification does not set archive_signature_verified');

-- 13. Backfill test: seed rows in various states and verify correct targeting.
-- Seed rows directly (bypassing trigger by using a realistic cloud_version placeholder).
-- Row A: PublicCloud + Verified + archive_signature_verified=true → MUST become Completed.
insert into public.submission_files(
  id, organization_id, submission_id, name, size_bytes, sha256, mime_type,
  cloud_object_path, transfer_status, sync_status, archive_signature_verified,
  source_mode, cloud_version, created_at, updated_at)
values (
  'b0800028-0000-0000-0000-000000000001',
  'b0100028-0000-0000-0000-000000000001',
  'b0600028-0000-0000-0000-000000000003',
  'old-verified.zip', 2048,
  'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
  'application/zip',
  'b0100028-0000-0000-0000-000000000001/public-submissions/b0000028-0000-0000-0000-000000000002/b0600028-0000-0000-0000-000000000003/b0800028-0000-0000-0000-000000000001.zip',
  'Verified', 'Synced', true, 'PublicCloud', 5, now(), now());

-- Row B: PublicCloud + Verified + archive_signature_verified=false → must NOT change (not yet verified).
insert into public.submission_files(
  id, organization_id, submission_id, name, size_bytes, sha256, mime_type,
  cloud_object_path, transfer_status, sync_status, archive_signature_verified,
  source_mode, cloud_version, created_at, updated_at)
values (
  'b0800028-0000-0000-0000-000000000002',
  'b0100028-0000-0000-0000-000000000001',
  'b0600028-0000-0000-0000-000000000004',
  'unverified-verified.zip', 2048,
  'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc',
  'application/zip',
  'b0100028-0000-0000-0000-000000000001/public-submissions/b0000028-0000-0000-0000-000000000002/b0600028-0000-0000-0000-000000000004/b0800028-0000-0000-0000-000000000002.zip',
  'Verified', 'Synced', false, 'PublicCloud', 5, now(), now());

-- Row C: PublicCloud + Queued (not Verified) → must NOT change.
insert into public.submission_files(
  id, organization_id, submission_id, name, size_bytes, sha256, mime_type,
  cloud_object_path, transfer_status, sync_status, archive_signature_verified,
  source_mode, cloud_version, created_at, updated_at)
values (
  'b0800028-0000-0000-0000-000000000003',
  'b0100028-0000-0000-0000-000000000001',
  'b0600028-0000-0000-0000-000000000005',
  'queued.zip', 2048,
  'dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd',
  'application/zip',
  'b0100028-0000-0000-0000-000000000001/public-submissions/b0000028-0000-0000-0000-000000000002/b0600028-0000-0000-0000-000000000005/b0800028-0000-0000-0000-000000000003.zip',
  'Queued', 'Pending', false, 'PublicCloud', 5, now(), now());

-- Row D: Already Completed → must NOT be re-stamped with a new cloud_version.
insert into public.submission_files(
  id, organization_id, submission_id, name, size_bytes, sha256, mime_type,
  cloud_object_path, transfer_status, sync_status, archive_signature_verified,
  source_mode, cloud_version, created_at, updated_at)
values (
  'b0800028-0000-0000-0000-000000000004',
  'b0100028-0000-0000-0000-000000000001',
  'b0600028-0000-0000-0000-000000000006',
  'already-completed.zip', 2048,
  'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee',
  'application/zip',
  'b0100028-0000-0000-0000-000000000001/public-submissions/b0000028-0000-0000-0000-000000000002/b0600028-0000-0000-0000-000000000006/b0800028-0000-0000-0000-000000000004.zip',
  'Completed', 'Synced', true, 'PublicCloud', 5, now(), now());

-- Capture the cloud_version of Row D before the backfill.
create temporary table pc28_row_d_before as
  select cloud_version as v_before from public.submission_files
  where id = 'b0800028-0000-0000-0000-000000000004';

-- Run the same backfill as in the migration (idempotent re-run).
update public.submission_files
set transfer_status = 'Completed',
    cloud_version   = private.next_public_cloud_version(),
    updated_at      = now()
where source_mode             = 'PublicCloud'
  and transfer_status         = 'Verified'
  and archive_signature_verified = true;

-- Row A must be Completed.
select is(
  (select transfer_status from public.submission_files
   where id = 'b0800028-0000-0000-0000-000000000001'),
  'Completed',
  'backfill: PublicCloud+Verified+verified=true becomes Completed');

-- Row A cloud_version must have increased from 5.
select ok(
  (select cloud_version from public.submission_files
   where id = 'b0800028-0000-0000-0000-000000000001') > 5,
  'backfill: cloud_version incremented for corrected row');

-- Row B must remain Verified (not verified = false).
select is(
  (select transfer_status from public.submission_files
   where id = 'b0800028-0000-0000-0000-000000000002'),
  'Verified',
  'backfill: PublicCloud+Verified+verified=false is not touched');

-- Row C must remain Queued.
select is(
  (select transfer_status from public.submission_files
   where id = 'b0800028-0000-0000-0000-000000000003'),
  'Queued',
  'backfill: Queued row is not touched');

-- Row D must remain at cloud_version 5 (already Completed, not re-stamped).
select is(
  (select cloud_version from public.submission_files
   where id = 'b0800028-0000-0000-0000-000000000004'),
  (select v_before from pc28_row_d_before),
  'backfill: already-Completed row cloud_version is unchanged');

select * from finish();
rollback;
