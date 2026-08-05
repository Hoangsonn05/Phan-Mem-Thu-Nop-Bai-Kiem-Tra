begin;

-- ET-PC-SUBMISSION-DOWNLOAD-R1
-- Root cause: verify_public_submission_archive wrote transfer_status = 'Verified',
-- which is not a member of the C# TransferStatus enum. PublicCloudPullWorker fell back
-- to TransferStatus.Queued, causing CompletedFileCount = 0 and blocking teacher download.
-- Fix: replace 'Verified' with 'Completed' throughout the function body.
-- This migration also backfills existing rows that carry the incorrect 'Verified' value.

-- A. Replace the active function with 'Completed' instead of 'Verified'.
--    All logic, checks, audit, security-definer, search_path, grants are preserved verbatim.
create or replace function public.verify_public_submission_archive(
  p_submission_id uuid,
  p_file_id uuid,
  p_observed_sha256 text,
  p_observed_size bigint,
  p_magic_type text)
returns void
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_file public.submission_files%rowtype;
  v_extension text;
begin
  if coalesce((select auth.jwt() ->> 'role'), '') <> 'service_role' then
    raise exception 'SERVICE_ROLE_REQUIRED' using errcode = '42501';
  end if;

  select f.* into v_file
  from public.submission_files f
  join public.submissions s on s.id = f.submission_id
  where f.id = p_file_id
    and f.submission_id = p_submission_id
    and f.source_mode = 'PublicCloud'
    and s.source_mode = 'PublicCloud'
    and s.status = 'Uploading'
  for update of f;
  if not found then
    raise exception 'PUBLIC_SUBMISSION_FILE_NOT_FOUND' using errcode = 'P0002';
  end if;

  v_extension := lower(substring(v_file.name from '\.([^.]+)$'));
  if p_observed_size <> v_file.size_bytes or p_observed_size <= 0 or p_observed_size > 10485760 then
    raise exception 'ARCHIVE_SIZE_MISMATCH' using errcode = '22023';
  end if;
  if lower(btrim(coalesce(p_observed_sha256, ''))) <> lower(v_file.sha256) then
    raise exception 'ARCHIVE_HASH_MISMATCH' using errcode = '22023';
  end if;
  if v_extension not in ('zip','rar','7z') or lower(coalesce(p_magic_type, '')) <> v_extension then
    raise exception 'ARCHIVE_SIGNATURE_INVALID' using errcode = '22023';
  end if;

  update public.submission_files
  set archive_signature_verified = true,
      transfer_status = 'Completed',
      sync_status = 'Synced',
      cloud_version = private.next_public_cloud_version(),
      updated_at = now()
  where id = v_file.id;

  insert into public.audit_logs(
    id, organization_id, session_id, actor_id, action, entity_type,
    entity_id, trace_id, before_json, after_json, created_at, updated_at)
  select gen_random_uuid(), s.organization_id, s.session_id, 'service_role',
    'VerifyPublicSubmissionArchive', 'submission_files', v_file.id::text,
    gen_random_uuid()::text,
    jsonb_build_object('verified', v_file.archive_signature_verified),
    jsonb_build_object('verified', true, 'sha256', lower(v_file.sha256),
      'sizeBytes', v_file.size_bytes, 'magicType', lower(p_magic_type)),
    now(), now()
  from public.submissions s where s.id = p_submission_id;
end
$function$;
revoke all on function public.verify_public_submission_archive(uuid,uuid,text,bigint,text)
  from public, anon, authenticated;
grant execute on function public.verify_public_submission_archive(uuid,uuid,text,bigint,text)
  to service_role;

-- B. Backfill existing rows that carry the incorrect 'Verified' status.
--    Scope: submission_files rows where
--      - source_mode = 'PublicCloud'          (column exists on submission_files per migration 20260722141147)
--      - transfer_status = 'Verified'         (incorrect value written by the old function body)
--      - archive_signature_verified = true    (the archive was genuinely checked; not a partial or failed row)
--    The update stamps a fresh cloud_version so PublicCloudPullWorker will pull the corrected row.
update public.submission_files
set transfer_status = 'Completed',
    cloud_version   = private.next_public_cloud_version(),
    updated_at      = now()
where source_mode             = 'PublicCloud'
  and transfer_status         = 'Verified'
  and archive_signature_verified = true;

-- C. Advance schema version so CheckHealthAsync unblocks and workers may synchronise.
update public.examtransfer_cloud_meta
set schema_version = 28,
    updated_at = now()
where id = 1;

commit;
