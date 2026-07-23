begin;

-- Ownership matrix implemented by this migration:
-- local-owned: classes, exams, exam_files, public_class_assignments,
-- exam_sessions/session state, control_policies, grades/publications.
-- cloud-owned: enrollment requests and members created by enrollment,
-- PublicCloud participants/devices/violations/commands/results/submissions/files
-- and quiz attempts/answers. Cloud-owned rows are pulled to SQLite and must not
-- be reverse-upserted by the local outbox.

-- Local-owned projections use this version for optimistic cloud writes.
do $columns$
declare v_table text;
begin
  foreach v_table in array array[
    'classes','exams','exam_files','quiz_questions','quiz_choices',
    'public_class_assignments','exam_sessions','control_policies','grades',
    'rubric_scores','graded_attachments','audit_logs','export_jobs','backups'
  ] loop
    execute format(
      'alter table public.%I add column if not exists cloud_version bigint not null default 1',
      v_table);
    execute format(
      'alter table public.%I alter column cloud_version set default 1',
      v_table);
    execute format(
      'update public.%I set cloud_version = 1 where cloud_version < 1',
      v_table);
  end loop;
end
$columns$;

-- Cloud-owned cursor columns. The global sequence makes every mutation
-- strictly orderable across polling pages; updated_at/id are deterministic
-- tie breakers and retained in the cursor contract.
alter table public.class_enrollment_requests
  add column if not exists source_mode text not null default 'PublicCloud',
  add column if not exists cloud_version bigint not null default 1;
alter table public.class_members
  add column if not exists source_mode text not null default 'Lan',
  add column if not exists cloud_version bigint not null default 1;
alter table public.public_device_commands
  add column if not exists source_mode text not null default 'PublicCloud',
  add column if not exists cloud_version bigint not null default 1,
  add column if not exists updated_at timestamptz not null default now();

do $defaults$
declare v_table text;
begin
  foreach v_table in array array[
    'class_enrollment_requests','class_members','session_participants',
    'public_device_connections','public_device_commands',
    'public_device_command_results','violations','submissions',
    'submission_files','quiz_attempts','quiz_answers'
  ] loop
    execute format('alter table public.%I alter column cloud_version set default 1', v_table);
  end loop;
end
$defaults$;

alter table public.class_enrollment_requests drop constraint if exists class_enrollment_requests_source_mode_check;
alter table public.class_enrollment_requests add constraint class_enrollment_requests_source_mode_check check (source_mode = 'PublicCloud');
alter table public.class_members drop constraint if exists class_members_source_mode_check;
alter table public.class_members add constraint class_members_source_mode_check check (source_mode in ('Lan','PublicCloud'));
alter table public.public_device_commands drop constraint if exists public_device_commands_source_mode_check;
alter table public.public_device_commands add constraint public_device_commands_source_mode_check check (source_mode = 'PublicCloud');

-- Compatibility: legacy LAN submissions may contain multiple files. The one
-- archive and idempotency constraints apply only to PublicCloud rows.
drop index if exists public.ux_submission_files_submission;
drop index if exists public.ux_public_submission_idempotency;
create unique index if not exists ux_public_submission_single_file
  on public.submission_files(submission_id)
  where source_mode = 'PublicCloud';
create unique index if not exists ux_public_submission_idempotency
  on public.submissions(participant_id, idempotency_key)
  where source_mode = 'PublicCloud' and idempotency_key is not null;

create or replace function public.enforce_student_submission_policy()
returns trigger
language plpgsql
set search_path = ''
as $function$
begin
  if new.source_mode <> 'PublicCloud' then
    return new;
  end if;
  if new.size_bytes <= 0 or new.size_bytes > 10485760 then
    raise exception 'SUBMISSION_TOO_LARGE' using errcode = '22023';
  end if;
  if lower(new.name) !~ '\.(zip|rar|7z)$' then
    raise exception 'SUBMISSION_ARCHIVE_REQUIRED' using errcode = '22023';
  end if;
  if exists (
    select 1 from public.submission_files f
    where f.submission_id = new.submission_id
      and f.source_mode = 'PublicCloud'
      and f.id <> new.id
  ) then
    raise exception 'SUBMISSION_FILE_COUNT_INVALID' using errcode = '23505';
  end if;
  return new;
end
$function$;
revoke all on function public.enforce_student_submission_policy() from public, anon, authenticated;

create or replace function private.enforce_public_submission_object_path()
returns trigger
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_user_id uuid;
  v_extension text;
  v_expected text;
begin
  if new.source_mode <> 'PublicCloud' then return new; end if;
  select p.user_id into v_user_id
  from public.submissions s
  join public.session_participants p on p.id = s.participant_id
  where s.id = new.submission_id
    and s.organization_id = new.organization_id
    and s.source_mode = 'PublicCloud';
  if not found or v_user_id is null then
    raise exception 'PUBLIC_SUBMISSION_OWNER_NOT_FOUND' using errcode = '23514';
  end if;
  v_extension := lower(substring(new.name from '\.([^.]+)$'));
  v_expected := new.organization_id::text || '/public-submissions/' ||
    v_user_id::text || '/' || new.submission_id::text || '/' ||
    new.id::text || '.' || v_extension;
  if tg_op = 'UPDATE' and old.cloud_object_path is distinct from v_expected then
    raise exception 'PUBLIC_SUBMISSION_PATH_IMMUTABLE' using errcode = '55000';
  end if;
  new.cloud_object_path := v_expected;
  return new;
end
$function$;
revoke all on function private.enforce_public_submission_object_path() from public, anon, authenticated, service_role;
drop trigger if exists trg_public_submission_object_path on public.submission_files;
create trigger trg_public_submission_object_path
before insert or update of cloud_object_path, submission_id, name on public.submission_files
for each row execute function private.enforce_public_submission_object_path();

-- Stamp every cloud-authored mutation. Values are never accepted from a
-- browser; all mutable student data crosses an RPC boundary.
create or replace function private.stamp_public_cloud_version()
returns trigger
language plpgsql
security definer
set search_path = ''
as $function$
begin
  new.cloud_version := private.next_public_cloud_version();
  new.updated_at := now();
  return new;
end
$function$;
revoke all on function private.stamp_public_cloud_version() from public, anon, authenticated, service_role;

do $triggers$
declare v_table text;
begin
  foreach v_table in array array[
    'class_enrollment_requests','public_device_connections',
    'public_device_commands','public_device_command_results'
  ] loop
    execute format('drop trigger if exists trg_stamp_public_cloud_version on public.%I', v_table);
    execute format(
      'create trigger trg_stamp_public_cloud_version before insert or update on public.%I for each row execute function private.stamp_public_cloud_version()',
      v_table);
  end loop;
end
$triggers$;

create or replace function private.stamp_source_public_cloud_version()
returns trigger
language plpgsql
security definer
set search_path = ''
as $function$
begin
  if new.source_mode = 'PublicCloud' then
    new.cloud_version := private.next_public_cloud_version();
    new.updated_at := now();
  end if;
  return new;
end
$function$;
revoke all on function private.stamp_source_public_cloud_version() from public, anon, authenticated, service_role;

do $triggers$
declare v_table text;
begin
  foreach v_table in array array[
    'class_members','session_participants','violations','submissions',
    'submission_files','quiz_attempts','quiz_answers'
  ] loop
    execute format('drop trigger if exists trg_stamp_public_cloud_version on public.%I', v_table);
    execute format(
      'create trigger trg_stamp_public_cloud_version before insert or update on public.%I for each row execute function private.stamp_source_public_cloud_version()',
      v_table);
  end loop;
end
$triggers$;

do $backfill_versions$
declare v_table text;
begin
  foreach v_table in array array[
    'class_enrollment_requests','public_device_connections',
    'public_device_commands','public_device_command_results'
  ] loop
    execute format('update public.%I set updated_at = updated_at where cloud_version < 1', v_table);
  end loop;
  foreach v_table in array array[
    'class_members','session_participants','violations','submissions',
    'submission_files','quiz_attempts','quiz_answers'
  ] loop
    execute format(
      'update public.%I set updated_at = updated_at where source_mode = ''PublicCloud'' and cloud_version < 1',
      v_table);
  end loop;
end
$backfill_versions$;

-- Enrollment-created members inherit cloud ownership without changing members
-- that were entered by a teacher in the local roster.
create or replace function private.mark_enrollment_member_cloud_owned()
returns trigger
language plpgsql
security definer
set search_path = ''
as $function$
begin
  if new.status = 'Approved' then
    update public.class_members
    set source_mode = 'PublicCloud', updated_at = now()
    where organization_id = new.organization_id
      and class_id = new.class_id
      and user_id = new.student_user_id;
  end if;
  return new;
end
$function$;
revoke all on function private.mark_enrollment_member_cloud_owned() from public, anon, authenticated, service_role;
drop trigger if exists trg_zz_mark_enrollment_member_cloud_owned on public.class_enrollment_requests;
create trigger trg_zz_mark_enrollment_member_cloud_owned
after insert or update of status on public.class_enrollment_requests
for each row execute function private.mark_enrollment_member_cloud_owned();

-- Cursor contract is per consumer and entity. It is service-only and can be
-- used by deployment diagnostics; desktop workers persist the same shape in
-- SQLite so a cloud outage never affects LAN operation.
alter table public.cloud_sync_cursors
  add column if not exists entity_name text,
  add column if not exists last_updated_at timestamptz,
  add column if not exists last_id uuid;
update public.cloud_sync_cursors
set entity_name = coalesce(entity_name, 'legacy'),
    last_id = coalesce(last_id, last_entity_id)
where entity_name is null or last_id is null;
alter table public.cloud_sync_cursors alter column entity_name set not null;
create unique index if not exists ux_cloud_sync_cursors_consumer_entity
  on public.cloud_sync_cursors(consumer_id, entity_name);

create index if not exists ix_enrollment_public_cursor
  on public.class_enrollment_requests(cloud_version, updated_at, id);
create index if not exists ix_members_public_cursor
  on public.class_members(cloud_version, updated_at, id)
  where source_mode = 'PublicCloud';
create index if not exists ix_connections_public_cursor
  on public.public_device_connections(cloud_version, updated_at, id);
create index if not exists ix_commands_public_cursor
  on public.public_device_commands(cloud_version, updated_at, command_id);
create index if not exists ix_command_results_public_cursor
  on public.public_device_command_results(cloud_version, updated_at, command_id);

-- Edge Functions verify the bytes, then this service-role-only RPC performs
-- the authoritative metadata check, version increment and audit atomically.
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
      transfer_status = 'Verified',
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

create or replace function public.get_public_exam_file_download(
  p_session_id uuid,
  p_file_id uuid)
returns table(object_path text, file_name text, size_bytes bigint, sha256 text)
language plpgsql
stable
security definer
set search_path = ''
as $function$
declare v_profile public.profiles%rowtype := private.require_active_student();
begin
  return query
  select f.cloud_object_path, f.name, f.size_bytes, lower(f.sha256)
  from public.exam_files f
  join public.exams e on e.id = f.exam_id and e.organization_id = f.organization_id
  join public.exam_sessions s on s.exam_id = e.id and s.organization_id = e.organization_id
  join public.session_participants p on p.session_id = s.id
  join public.class_members m on m.class_id = s.class_id
  join public.public_class_assignments a
    on a.class_id = s.class_id and a.exam_id = e.id
  where s.id = p_session_id
    and f.id = p_file_id
    and s.access_mode = 'PublicCloud'
    and s.status in ('Waiting','InProgress','Collecting')
    and p.user_id = v_profile.id
    and p.status = 'Approved'
    and p.source_mode = 'PublicCloud'
    and m.user_id = v_profile.id
    and m.organization_id = v_profile.organization_id
    and a.organization_id = v_profile.organization_id
    and (a.available_from is null or a.available_from <= now())
    and (a.available_until is null or a.available_until >= now())
    and f.cloud_object_path is not null;
  if not found then
    raise exception 'PUBLIC_EXAM_FILE_FORBIDDEN' using errcode = '42501';
  end if;
end
$function$;
revoke all on function public.get_public_exam_file_download(uuid,uuid) from public, anon;
grant execute on function public.get_public_exam_file_download(uuid,uuid) to authenticated;

-- One authenticated capability check replaces a collection of optimistic
-- health probes. Workers are allowed to run only when this contract matches.
create or replace function public.get_examtransfer_cloud_capabilities()
returns jsonb
language plpgsql
stable
security definer
set search_path = ''
as $function$
begin
  if (select auth.uid()) is null
     and coalesce((select auth.jwt() ->> 'role'), '') <> 'service_role' then
    raise exception 'AUTHENTICATION_REQUIRED' using errcode = '28000';
  end if;
  return jsonb_build_object(
    'schemaVersion', (select schema_version from public.examtransfer_cloud_meta where id = 1),
    'criticalRpcs', jsonb_build_array(
      'join_public_session','init_public_submission','finalize_public_submission',
      'upsert_public_device_heartbeat','ack_public_device_command',
      'start_public_quiz_attempt','save_public_quiz_answers',
      'finalize_public_quiz_attempt','verify_public_submission_archive',
      'get_public_exam_file_download'),
    'buckets', coalesce((
      select jsonb_agg(id order by id) from storage.buckets
      where id in ('exam-archives','public-submission-archives')
    ), '[]'::jsonb)
  );
end
$function$;
revoke all on function public.get_examtransfer_cloud_capabilities() from public, anon;
grant execute on function public.get_examtransfer_cloud_capabilities() to authenticated, service_role;

-- Private Realtime topics. Students can receive session broadcasts and send
-- only their own telemetry; only staff can broadcast session/device commands.
drop policy if exists examtransfer_broadcast_receive on realtime.messages;
drop policy if exists examtransfer_broadcast_send on realtime.messages;

create policy examtransfer_broadcast_receive on realtime.messages
for select to authenticated using (
  extension = 'broadcast' and (
    exists (
      select 1 from public.public_device_connections c
      where c.user_id = (select auth.uid()) and (
        (select realtime.topic()) = 'exam-session:' || c.session_id::text
        or (select realtime.topic()) = 'exam-session:' || c.session_id::text || ':device:' || c.device_id))
    or exists (
      select 1 from public.profiles p
      join public.exam_sessions s on s.organization_id = p.organization_id
      join public.exams e on e.id = s.exam_id and e.organization_id = s.organization_id
      where p.id = (select auth.uid()) and p.is_active = true
        and p.role in ('Admin','Teacher') and s.access_mode = 'PublicCloud'
        and (p.role = 'Admin' or e.created_by = p.id)
        and ((select realtime.topic()) = 'exam-session:' || s.id::text
          or exists (select 1 from public.public_device_connections c where c.session_id = s.id and (
            (select realtime.topic()) = 'exam-session:' || s.id::text || ':device:' || c.device_id
            or (select realtime.topic()) = 'exam-session:' || s.id::text || ':telemetry:' || c.device_id))))));

create policy examtransfer_broadcast_send on realtime.messages
for insert to authenticated with check (
  extension = 'broadcast' and (
    exists (
      select 1 from public.public_device_connections c
      where c.user_id = (select auth.uid())
        and (select realtime.topic()) = 'exam-session:' || c.session_id::text || ':telemetry:' || c.device_id)
    or exists (
      select 1 from public.profiles p
      join public.exam_sessions s on s.organization_id = p.organization_id
      join public.exams e on e.id = s.exam_id and e.organization_id = s.organization_id
      where p.id = (select auth.uid()) and p.is_active = true
        and p.role in ('Admin','Teacher') and s.access_mode = 'PublicCloud'
        and (p.role = 'Admin' or e.created_by = p.id)
        and ((select realtime.topic()) = 'exam-session:' || s.id::text
          or exists (select 1 from public.public_device_connections c where c.session_id = s.id
            and (select realtime.topic()) = 'exam-session:' || s.id::text || ':device:' || c.device_id)))));

insert into public.examtransfer_cloud_meta(id, schema_version, updated_at)
values (1, 14, now())
on conflict (id) do update
set schema_version = excluded.schema_version,
    updated_at = excluded.updated_at;

commit;
