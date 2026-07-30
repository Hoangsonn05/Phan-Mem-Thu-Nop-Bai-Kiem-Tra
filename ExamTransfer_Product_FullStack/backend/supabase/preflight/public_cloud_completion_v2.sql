-- Read-only compatibility report. Run before applying completion V2.
-- It intentionally never updates or deletes legacy data.

select 'duplicate_public_submission_idempotency' as check_name,
       participant_id::text as owner_id,
       idempotency_key as value,
       count(*) as affected_rows
from public.submissions
where source_mode = 'PublicCloud' and idempotency_key is not null
group by participant_id, idempotency_key
having count(*) > 1;

select 'multiple_public_archives_per_submission' as check_name,
       submission_id::text as owner_id,
       null::text as value,
       count(*) as affected_rows
from public.submission_files
where source_mode = 'PublicCloud'
group by submission_id
having count(*) > 1;

select 'invalid_public_archive_metadata' as check_name,
       id::text as file_id,
       name,
       size_bytes,
       sha256,
       cloud_object_path
from public.submission_files
where source_mode = 'PublicCloud'
  and (
    size_bytes <= 0 or size_bytes > 10485760
    or lower(name) !~ '\.(zip|rar|7z)$'
    or lower(coalesce(sha256, '')) !~ '^[0-9a-f]{64}$'
    or cloud_object_path is null
  )
order by created_at, id;

select 'public_archive_path_mismatch' as check_name,
       f.id::text as file_id,
       f.cloud_object_path,
       s.id::text as submission_id,
       p.user_id::text as user_id
from public.submission_files f
join public.submissions s on s.id = f.submission_id
join public.session_participants p on p.id = s.participant_id
where f.source_mode = 'PublicCloud'
  and f.cloud_object_path not like
    f.organization_id::text || '/public-submissions/' || p.user_id::text ||
    '/' || s.id::text || '/' || f.id::text || '.%'
order by f.created_at, f.id;
