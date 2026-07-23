begin;

create sequence if not exists public.public_cloud_version_seq as bigint start with 1;
revoke all on sequence public.public_cloud_version_seq from public, anon, authenticated;

alter table public.classes
  add column if not exists access_mode text not null default 'Private',
  add column if not exists enrollment_open boolean not null default false,
  add column if not exists require_enrollment_approval boolean not null default true,
  add column if not exists enrollment_code_hash text,
  add column if not exists enrollment_opened_at timestamptz,
  add column if not exists enrollment_closed_at timestamptz,
  add column if not exists public_version integer not null default 0;

alter table public.classes drop constraint if exists classes_access_mode_check;
alter table public.classes add constraint classes_access_mode_check check (access_mode in ('Private', 'Public'));
alter table public.classes drop constraint if exists classes_public_version_check;
alter table public.classes add constraint classes_public_version_check check (public_version >= 0);

alter table public.exam_sessions
  add column if not exists access_mode text not null default 'LanOnly',
  add column if not exists cloud_version bigint not null default 0;
alter table public.exam_sessions drop constraint if exists exam_sessions_access_mode_check;
alter table public.exam_sessions add constraint exam_sessions_access_mode_check check (access_mode in ('LanOnly', 'PublicCloud'));

alter table public.violations
  add column if not exists class_id uuid references public.classes(id) on delete set null,
  add column if not exists device_id text,
  add column if not exists evidence_metadata jsonb,
  add column if not exists status text not null default 'Open',
  add column if not exists resolved_by uuid,
  add column if not exists resolved_at timestamptz;
alter table public.session_participants
  add column if not exists source_mode text not null default 'Lan',
  add column if not exists cloud_version bigint not null default 0;
alter table public.submissions
  add column if not exists source_mode text not null default 'Lan',
  add column if not exists cloud_version bigint not null default 0;
alter table public.submission_files
  add column if not exists archive_signature_verified boolean not null default false,
  add column if not exists source_mode text not null default 'Lan',
  add column if not exists cloud_version bigint not null default 0;
alter table public.violations
  add column if not exists source_mode text not null default 'Lan',
  add column if not exists cloud_version bigint not null default 0;
alter table public.quiz_attempts
  add column if not exists source_mode text not null default 'Lan',
  add column if not exists cloud_version bigint not null default 0;
alter table public.quiz_answers
  add column if not exists source_mode text not null default 'Lan',
  add column if not exists cloud_version bigint not null default 0;

do $migration$
declare
  v_table text;
begin
  foreach v_table in array array[
    'session_participants','submissions','submission_files','violations',
    'quiz_attempts','quiz_answers'
  ] loop
    if not exists (
      select 1 from pg_constraint
      where conname = v_table || '_source_mode_check'
        and conrelid = ('public.' || v_table)::regclass
    ) then
      execute format(
        'alter table public.%I add constraint %I check (source_mode in (''Lan'',''PublicCloud''))',
        v_table, v_table || '_source_mode_check');
    end if;
  end loop;
end
$migration$;

create table if not exists public.class_enrollment_requests (
  id uuid primary key default gen_random_uuid(),
  organization_id uuid not null references public.organizations(id) on delete restrict,
  class_id uuid not null references public.classes(id) on delete cascade,
  student_user_id uuid not null references auth.users(id) on delete cascade,
  student_code text not null,
  status text not null default 'Pending' check (status in ('Pending','Approved','Rejected','Cancelled')),
  requested_at timestamptz not null default now(),
  decided_at timestamptz,
  decided_by uuid references auth.users(id) on delete set null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (class_id, student_user_id)
);

create table if not exists public.public_class_assignments (
  id uuid primary key default gen_random_uuid(),
  organization_id uuid not null references public.organizations(id) on delete restrict,
  class_id uuid not null references public.classes(id) on delete cascade,
  exam_id uuid not null references public.exams(id) on delete cascade,
  assigned_at timestamptz not null default now(),
  available_from timestamptz,
  available_until timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (class_id, exam_id)
);

create table if not exists public.public_device_connections (
  id uuid primary key default gen_random_uuid(),
  organization_id uuid not null references public.organizations(id) on delete restrict,
  session_id uuid not null references public.exam_sessions(id) on delete cascade,
  participant_id uuid not null references public.session_participants(id) on delete cascade,
  user_id uuid not null references auth.users(id) on delete cascade,
  device_id text not null,
  connection_state text not null check (connection_state in ('Offline','Connecting','Online','Reconnecting','Degraded')),
  heartbeat_at timestamptz not null,
  foreground_application text,
  running_process_summary jsonb,
  policy_state text,
  lock_state text,
  violation_count integer not null default 0 check (violation_count >= 0),
  app_version text,
  agent_version text,
  policy_lease_expires_at timestamptz,
  last_policy_renewal_at timestamptz,
  source_mode text not null default 'PublicCloud' check (source_mode = 'PublicCloud'),
  cloud_version bigint not null default 1 check (cloud_version > 0),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (session_id, device_id)
);

create table if not exists public.public_device_commands (
  command_id uuid primary key default gen_random_uuid(),
  organization_id uuid not null references public.organizations(id) on delete restrict,
  session_id uuid not null references public.exam_sessions(id) on delete cascade,
  device_id text not null,
  command_type text not null check (command_type in ('ApplyPolicy','UpdatePolicy','ShowWarning','LockExamApplication','UnlockExamApplication','ForceFocusExamApplication','RequestDeviceSnapshot','RequestRunningProcesses','ForceSubmit','EndDeviceSession','ClearPolicy')),
  payload jsonb not null default '{}'::jsonb,
  created_at timestamptz not null default now(),
  expires_at timestamptz not null,
  issued_by uuid not null references auth.users(id) on delete restrict,
  signature text not null check (length(signature) >= 64),
  retry_count integer not null default 0 check (retry_count >= 0),
  last_retry_at timestamptz,
  check (expires_at > created_at)
);

create table if not exists public.public_device_command_results (
  command_id uuid primary key references public.public_device_commands(command_id) on delete cascade,
  organization_id uuid not null references public.organizations(id) on delete restrict,
  device_id text not null,
  status text not null check (status in ('Received','Executed','Failed','Expired','Ignored')),
  received_at timestamptz not null,
  executed_at timestamptz,
  error_code text,
  error_message text,
  source_mode text not null default 'PublicCloud' check (source_mode = 'PublicCloud'),
  cloud_version bigint not null default 1 check (cloud_version > 0),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create index if not exists ix_enrollment_requests_student on public.class_enrollment_requests(student_user_id, status, class_id);
create index if not exists ix_public_assignments_class_window on public.public_class_assignments(class_id, available_from, available_until);
create index if not exists ix_public_connections_user_session on public.public_device_connections(user_id, session_id, heartbeat_at desc);
create index if not exists ix_public_connections_device on public.public_device_connections(session_id, device_id);
create index if not exists ix_public_commands_device_pending on public.public_device_commands(device_id, expires_at, created_at);
create index if not exists ix_public_violations_session_device on public.violations(session_id, device_id, occurred_at desc);
create index if not exists ix_class_members_user_class on public.class_members(user_id, class_id);
create index if not exists ix_session_participants_user_session on public.session_participants(user_id, session_id);
create unique index if not exists ux_public_submission_idempotency on public.submissions(participant_id, idempotency_key) where idempotency_key is not null;
create unique index if not exists ux_submission_files_submission on public.submission_files(submission_id);
create index if not exists ix_participants_public_cursor on public.session_participants(cloud_version, id) where source_mode = 'PublicCloud';
create index if not exists ix_submissions_public_cursor on public.submissions(cloud_version, id) where source_mode = 'PublicCloud';
create index if not exists ix_submission_files_public_cursor on public.submission_files(cloud_version, id) where source_mode = 'PublicCloud';
create index if not exists ix_violations_public_cursor on public.violations(cloud_version, id) where source_mode = 'PublicCloud';
create index if not exists ix_quiz_attempts_public_cursor on public.quiz_attempts(cloud_version, id) where source_mode = 'PublicCloud';
create index if not exists ix_quiz_answers_public_cursor on public.quiz_answers(cloud_version, id) where source_mode = 'PublicCloud';

create or replace function public.enforce_student_submission_policy()
returns trigger language plpgsql set search_path = '' as $function$
begin
  if new.size_bytes <= 0 or new.size_bytes > 10485760 then raise exception 'SUBMISSION_TOO_LARGE' using errcode = '22023'; end if;
  if lower(new.name) !~ '\.(zip|rar|7z)$' then raise exception 'SUBMISSION_ARCHIVE_REQUIRED' using errcode = '22023'; end if;
  if exists (select 1 from public.submission_files f where f.submission_id = new.submission_id and f.id <> new.id) then
    raise exception 'SUBMISSION_FILE_COUNT_INVALID' using errcode = '23505';
  end if;
  return new;
end
$function$;
revoke all on function public.enforce_student_submission_policy() from public, anon, authenticated;
drop trigger if exists trg_enforce_student_submission_policy on public.submission_files;
create trigger trg_enforce_student_submission_policy before insert or update of name, size_bytes, submission_id on public.submission_files
for each row execute function public.enforce_student_submission_policy();

create or replace function public.enforce_public_submission_finalize()
returns trigger language plpgsql set search_path = '' as $function$
begin
  if new.status in ('Submitted','LateSubmitted') and old.status is distinct from new.status
     and exists (select 1 from public.exam_sessions s where s.id = new.session_id and s.access_mode = 'PublicCloud') then
    if (select count(*) from public.submission_files f where f.submission_id = new.id and f.archive_signature_verified) <> 1 then
      raise exception 'SUBMISSION_ARCHIVE_REQUIRED' using errcode = '22023';
    end if;
  end if;
  return new;
end
$function$;
revoke all on function public.enforce_public_submission_finalize() from public, anon, authenticated;
drop trigger if exists trg_enforce_public_submission_finalize on public.submissions;
create trigger trg_enforce_public_submission_finalize before update of status on public.submissions
for each row execute function public.enforce_public_submission_finalize();

alter table public.class_enrollment_requests enable row level security;
alter table public.public_class_assignments enable row level security;
alter table public.public_device_connections enable row level security;
alter table public.public_device_commands enable row level security;
alter table public.public_device_command_results enable row level security;

create or replace function public.request_public_class_enrollment(p_enrollment_code text, p_student_code text)
returns uuid
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_user_id uuid := auth.uid();
  v_class public.classes%rowtype;
  v_profile public.profiles%rowtype;
  v_request_id uuid;
begin
  if v_user_id is null then raise exception 'AUTHENTICATION_REQUIRED' using errcode = '28000'; end if;
  select * into v_profile from public.profiles where id = v_user_id and role = 'Student' and is_active = true;
  if not found or lower(btrim(v_profile.student_code)) <> lower(btrim(p_student_code)) then
    raise exception 'STUDENT_IDENTITY_MISMATCH' using errcode = '28000';
  end if;
  select * into v_class
  from public.classes
  where organization_id = v_profile.organization_id
    and access_mode = 'Public' and enrollment_open
    and enrollment_code_hash is not null
    and enrollment_code_hash = crypt(btrim(p_enrollment_code), enrollment_code_hash)
  limit 1;
  if not found then raise exception 'ENROLLMENT_CODE_INVALID' using errcode = '22023'; end if;

  insert into public.class_enrollment_requests(organization_id, class_id, student_user_id, student_code, status, decided_at)
  values (v_class.organization_id, v_class.id, v_user_id, btrim(v_profile.student_code),
          case when v_class.require_enrollment_approval then 'Pending' else 'Approved' end,
          case when v_class.require_enrollment_approval then null else now() end)
  on conflict (class_id, student_user_id) do update
    set status = case when class_enrollment_requests.status = 'Approved' then 'Approved' else excluded.status end,
        requested_at = case when class_enrollment_requests.status = 'Approved' then class_enrollment_requests.requested_at else now() end,
        decided_at = case when class_enrollment_requests.status = 'Approved' then class_enrollment_requests.decided_at else excluded.decided_at end,
        decided_by = case when class_enrollment_requests.status = 'Approved' then class_enrollment_requests.decided_by else null end,
        updated_at = now()
  returning id into v_request_id;

  if not v_class.require_enrollment_approval then
    insert into public.class_members(id, organization_id, class_id, user_id, student_code, display_name, email, created_at, updated_at)
    values (gen_random_uuid(), v_class.organization_id, v_class.id, v_user_id, btrim(v_profile.student_code), v_profile.display_name, null, now(), now())
    on conflict (class_id, student_code) do update
      set user_id = excluded.user_id, display_name = excluded.display_name, email = excluded.email, updated_at = now();
  end if;
  return v_request_id;
end
$function$;

create or replace function public.sync_approved_enrollment_member()
returns trigger language plpgsql security definer set search_path = '' as $function$
declare
  v_profile public.profiles%rowtype;
begin
  if new.status = 'Approved' and old.status is distinct from new.status then
    select * into v_profile from public.profiles where id = new.student_user_id and organization_id = new.organization_id;
    if not found or v_profile.role <> 'Student' or lower(btrim(v_profile.student_code)) <> lower(btrim(new.student_code)) then
      raise exception 'STUDENT_IDENTITY_MISMATCH' using errcode = '28000';
    end if;
    insert into public.class_members(id, organization_id, class_id, user_id, student_code, display_name, email, created_at, updated_at)
    values (gen_random_uuid(), new.organization_id, new.class_id, new.student_user_id, btrim(new.student_code), v_profile.display_name, null, now(), now())
    on conflict (class_id, student_code) do update
      set user_id = excluded.user_id, display_name = excluded.display_name, email = excluded.email, updated_at = now();
  end if;
  return new;
end
$function$;
revoke all on function public.sync_approved_enrollment_member() from public, anon, authenticated;
drop trigger if exists trg_sync_approved_enrollment_member on public.class_enrollment_requests;
create trigger trg_sync_approved_enrollment_member after update of status on public.class_enrollment_requests
for each row execute function public.sync_approved_enrollment_member();

create or replace function public.set_public_class_enrollment_code(p_class_id uuid, p_enrollment_code text)
returns void
language plpgsql
security definer
set search_path = ''
as $function$
begin
  if auth.uid() is null or (select public.current_examtransfer_role()) not in ('Admin','Teacher') then
    raise exception 'FORBIDDEN' using errcode = '42501';
  end if;
  if length(btrim(p_enrollment_code)) < 6 then raise exception 'ENROLLMENT_CODE_TOO_SHORT' using errcode = '22023'; end if;
  update public.classes
  set access_mode = 'Public', enrollment_code_hash = crypt(btrim(p_enrollment_code), gen_salt('bf')),
      enrollment_open = true, enrollment_opened_at = now(), enrollment_closed_at = null,
      public_version = public_version + 1, updated_at = now()
  where id = p_class_id and organization_id = (select public.current_organization_id());
  if not found then raise exception 'CLASS_NOT_FOUND' using errcode = 'P0002'; end if;
end
$function$;

create or replace function public.close_public_class_enrollment(p_class_id uuid)
returns void
language plpgsql
security definer
set search_path = ''
as $function$
begin
  if auth.uid() is null or (select public.current_examtransfer_role()) not in ('Admin','Teacher') then
    raise exception 'FORBIDDEN' using errcode = '42501';
  end if;
  update public.classes
  set enrollment_open = false, enrollment_code_hash = null, enrollment_closed_at = now(),
      public_version = public_version + 1, updated_at = now()
  where id = p_class_id and organization_id = (select public.current_organization_id());
  if not found then raise exception 'CLASS_NOT_FOUND' using errcode = 'P0002'; end if;
end
$function$;

revoke all on function public.request_public_class_enrollment(text, text) from public, anon;
revoke all on function public.set_public_class_enrollment_code(uuid, text) from public, anon;
revoke all on function public.close_public_class_enrollment(uuid) from public, anon;
grant execute on function public.request_public_class_enrollment(text, text) to authenticated;
grant execute on function public.set_public_class_enrollment_code(uuid, text) to authenticated;
grant execute on function public.close_public_class_enrollment(uuid) to authenticated;

-- Internal identity guard shared by the public RPC boundary. It is deliberately
-- outside the exposed public schema and cannot be called by API roles.
create schema if not exists private;
revoke all on schema private from public, anon, authenticated;

create or replace function private.require_active_student()
returns public.profiles
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype;
begin
  if (select auth.uid()) is null then
    raise exception 'AUTHENTICATION_REQUIRED' using errcode = '28000';
  end if;
  select * into v_profile
  from public.profiles
  where id = (select auth.uid())
    and role = 'Student'
    and is_active = true
    and organization_id is not null;
  if not found then
    raise exception 'ACTIVE_STUDENT_REQUIRED' using errcode = '42501';
  end if;
  return v_profile;
end
$function$;
revoke all on function private.require_active_student() from public, anon, authenticated, service_role;

create or replace function private.next_public_cloud_version()
returns bigint
language sql
security definer
set search_path = ''
as $function$
  select nextval('public.public_cloud_version_seq'::regclass)
$function$;
revoke all on function private.next_public_cloud_version() from public, anon, authenticated, service_role;

create or replace function public.join_public_session(
  p_session_id uuid,
  p_device_id text,
  p_machine_name text default null,
  p_app_version text default null,
  p_capability_json jsonb default '{}'::jsonb)
returns uuid
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
  v_session public.exam_sessions%rowtype;
  v_member public.class_members%rowtype;
  v_participant_id uuid;
  v_status text;
begin
  if length(btrim(coalesce(p_device_id, ''))) not between 1 and 128 then
    raise exception 'DEVICE_ID_INVALID' using errcode = '22023';
  end if;
  if pg_column_size(coalesce(p_capability_json, '{}'::jsonb)) > 32768 then
    raise exception 'CAPABILITY_PAYLOAD_TOO_LARGE' using errcode = '22023';
  end if;
  perform pg_advisory_xact_lock(hashtextextended(p_session_id::text, 0));

  select s.* into v_session
  from public.exam_sessions s
  join public.exams e
    on e.id = s.exam_id and e.organization_id = s.organization_id
  join public.classes c
    on c.id = s.class_id and c.organization_id = s.organization_id
  where s.id = p_session_id
    and s.organization_id = v_profile.organization_id
    and s.access_mode = 'PublicCloud'
    and c.access_mode = 'Public';
  if not found then raise exception 'PUBLIC_SESSION_NOT_FOUND' using errcode = 'P0002'; end if;

  select m.* into v_member
  from public.class_members m
  where m.class_id = v_session.class_id
    and m.organization_id = v_profile.organization_id
    and m.user_id = v_profile.id
    and lower(btrim(m.student_code)) = lower(btrim(v_profile.student_code));
  if not found then raise exception 'CLASS_MEMBERSHIP_REQUIRED' using errcode = '42501'; end if;

  if not exists (
    select 1 from public.public_class_assignments a
    where a.organization_id = v_profile.organization_id
      and a.class_id = v_session.class_id
      and a.exam_id = v_session.exam_id
      and (a.available_from is null or a.available_from <= now())
      and (a.available_until is null or a.available_until >= now())
  ) then raise exception 'PUBLIC_ASSIGNMENT_UNAVAILABLE' using errcode = '42501'; end if;

  select id into v_participant_id
  from public.session_participants
  where session_id = p_session_id and user_id = v_profile.id;
  if found then return v_participant_id; end if;

  if v_session.status <> 'Waiting' or not v_session.accepting_participants then
    raise exception 'SESSION_NOT_ACCEPTING_PARTICIPANTS' using errcode = '55000';
  end if;
  if v_session.capacity is not null and (
    select count(*) from public.session_participants p
    where p.session_id = p_session_id and p.status <> 'Rejected'
  ) >= v_session.capacity then
    raise exception 'SESSION_CAPACITY_REACHED' using errcode = '54000';
  end if;

  v_participant_id := gen_random_uuid();
  v_status := case when v_session.auto_approve then 'Approved' else 'PendingApproval' end;
  insert into public.session_participants(
    id, organization_id, session_id, user_id, student_code, display_name,
    class_name, device_id, machine_name, app_version, status, joined_at,
    approved_at, last_seen_at, download_status, submission_status,
    extra_time_minutes, resubmit_allowed, capability_json, source_mode,
    cloud_version, created_at, updated_at)
  values (
    v_participant_id, v_profile.organization_id, p_session_id, v_profile.id,
    btrim(v_profile.student_code), v_profile.display_name, null, btrim(p_device_id),
    nullif(btrim(p_machine_name), ''), nullif(btrim(p_app_version), ''), v_status,
    now(), case when v_status = 'Approved' then now() else null end, now(),
    'NotStarted', 'NotStarted', 0, false, coalesce(p_capability_json, '{}'::jsonb),
    'PublicCloud', private.next_public_cloud_version(), now(), now());
  return v_participant_id;
end
$function$;

create or replace function public.init_public_submission(
  p_session_id uuid,
  p_idempotency_key text,
  p_file_name text,
  p_size_bytes bigint,
  p_sha256 text)
returns uuid
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
  v_session public.exam_sessions%rowtype;
  v_participant public.session_participants%rowtype;
  v_submission_id uuid;
  v_attempt integer;
  v_deadline timestamptz;
  v_safe_name text := btrim(coalesce(p_file_name, ''));
begin
  if length(btrim(coalesce(p_idempotency_key, ''))) not between 8 and 128 then
    raise exception 'IDEMPOTENCY_KEY_INVALID' using errcode = '22023';
  end if;
  if p_size_bytes <= 0 or p_size_bytes > 10485760 then
    raise exception 'SUBMISSION_TOO_LARGE' using errcode = '22023';
  end if;
  if length(v_safe_name) not between 1 and 255
     or lower(v_safe_name) !~ '\.(zip|rar|7z)$' or v_safe_name ~ '[/\\]' then
    raise exception 'SUBMISSION_ARCHIVE_REQUIRED' using errcode = '22023';
  end if;
  if lower(btrim(coalesce(p_sha256, ''))) !~ '^[0-9a-f]{64}$' then
    raise exception 'SHA256_INVALID' using errcode = '22023';
  end if;
  perform pg_advisory_xact_lock(hashtextextended(p_session_id::text || ':' || v_profile.id::text, 0));

  select s.* into v_session
  from public.exam_sessions s
  join public.exams e on e.id = s.exam_id and e.organization_id = s.organization_id
  join public.classes c on c.id = s.class_id and c.organization_id = s.organization_id
  where s.id = p_session_id
    and s.organization_id = v_profile.organization_id
    and s.access_mode = 'PublicCloud'
    and e.delivery_type = 'FileSubmission';
  if not found then raise exception 'PUBLIC_FILE_SESSION_NOT_FOUND' using errcode = 'P0002'; end if;
  if v_session.status not in ('InProgress','Collecting') then
    raise exception 'SUBMISSION_WINDOW_CLOSED' using errcode = '55000';
  end if;

  select * into v_participant
  from public.session_participants
  where session_id = p_session_id
    and organization_id = v_profile.organization_id
    and user_id = v_profile.id
    and status = 'Approved'
    and source_mode = 'PublicCloud'
    and exists (
      select 1 from public.class_members m
      where m.class_id = v_session.class_id and m.user_id = v_profile.id
        and m.organization_id = v_profile.organization_id);
  if not found then raise exception 'APPROVED_PARTICIPANT_REQUIRED' using errcode = '42501'; end if;

  select id into v_submission_id
  from public.submissions
  where participant_id = v_participant.id
    and idempotency_key = btrim(p_idempotency_key);
  if found then return v_submission_id; end if;

  if exists (
    select 1 from public.submissions
    where participant_id = v_participant.id
      and status in ('Submitted','LateSubmitted')
  ) and not v_participant.resubmit_allowed then
    raise exception 'RESUBMISSION_NOT_ALLOWED' using errcode = '55000';
  end if;

  v_attempt := coalesce((select max(attempt_number) from public.submissions where participant_id = v_participant.id), 0) + 1;
  v_deadline := v_session.started_at
    + make_interval(mins => (
        select e.duration_minutes from public.exams e where e.id = v_session.exam_id))
    + make_interval(mins => greatest(v_participant.extra_time_minutes, 0));
  if v_session.started_at is null then raise exception 'SESSION_NOT_STARTED' using errcode = '55000'; end if;
  select least(v_deadline, coalesce(a.available_until, v_deadline)) into v_deadline
  from public.public_class_assignments a
  where a.organization_id = v_profile.organization_id
    and a.class_id = v_session.class_id and a.exam_id = v_session.exam_id
    and (a.available_from is null or a.available_from <= now())
    and (a.available_until is null or a.available_until >= now());
  if not found then raise exception 'PUBLIC_ASSIGNMENT_UNAVAILABLE' using errcode = '42501'; end if;

  v_submission_id := gen_random_uuid();
  insert into public.submissions(
    id, organization_id, session_id, participant_id, attempt_number, status,
    deadline_at, is_late, is_official, idempotency_key, source_mode,
    cloud_version, created_at, updated_at)
  values (
    v_submission_id, v_profile.organization_id, p_session_id, v_participant.id,
    v_attempt, 'Uploading', v_deadline, false, false, btrim(p_idempotency_key),
    'PublicCloud', private.next_public_cloud_version(), now(), now());

  insert into public.submission_files(
    id, organization_id, submission_id, name, stored_name, mime_type,
    size_bytes, sha256, transfer_status, sync_status, cloud_object_path,
    archive_signature_verified, source_mode, cloud_version, created_at, updated_at)
  values (
    gen_random_uuid(), v_profile.organization_id, v_submission_id, v_safe_name,
    v_safe_name, 'application/octet-stream', p_size_bytes, lower(btrim(p_sha256)),
    'Pending', 'Pending', v_profile.organization_id::text || '/public-submissions/' ||
      v_profile.id::text || '/' || v_submission_id::text || '/' || v_safe_name,
    false, 'PublicCloud', private.next_public_cloud_version(), now(), now());

  update public.session_participants
  set submission_status = 'Uploading', resubmit_allowed = false,
      resubmit_reason = null, cloud_version = private.next_public_cloud_version(), updated_at = now()
  where id = v_participant.id;
  return v_submission_id;
end
$function$;

create or replace function public.finalize_public_submission(
  p_submission_id uuid,
  p_idempotency_key text)
returns text
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
  v_submission public.submissions%rowtype;
  v_received timestamptz := now();
  v_receipt text;
begin
  perform pg_advisory_xact_lock(hashtextextended(p_submission_id::text, 0));
  select s.* into v_submission
  from public.submissions s
  join public.session_participants p on p.id = s.participant_id
  join public.exam_sessions es on es.id = s.session_id
  join public.class_members m
    on m.class_id = es.class_id and m.user_id = p.user_id
   and m.organization_id = es.organization_id
  where s.id = p_submission_id
    and s.organization_id = v_profile.organization_id
    and s.source_mode = 'PublicCloud'
    and p.user_id = v_profile.id
    and p.session_id = s.session_id
    and es.access_mode = 'PublicCloud'
    and es.status in ('InProgress','Collecting');
  if not found then raise exception 'PUBLIC_SUBMISSION_NOT_FOUND' using errcode = 'P0002'; end if;
  if v_submission.idempotency_key <> btrim(coalesce(p_idempotency_key, '')) then
    raise exception 'IDEMPOTENCY_KEY_MISMATCH' using errcode = '22023';
  end if;
  if v_submission.status in ('Submitted','LateSubmitted') then
    return v_submission.receipt_code;
  end if;
  if v_submission.status not in ('Uploading','Verifying') then
    raise exception 'SUBMISSION_STATE_INVALID' using errcode = '55000';
  end if;
  if (
    select count(*) from public.submission_files f
    where f.submission_id = p_submission_id
      and f.archive_signature_verified = true
      and f.source_mode = 'PublicCloud'
  ) <> 1 then
    raise exception 'ARCHIVE_NOT_VERIFIED_BY_BACKEND' using errcode = '55000';
  end if;
  if not exists (
    select 1
    from public.submission_files f
    join storage.objects o
      on o.bucket_id = 'public-submission-archives'
     and o.name = f.cloud_object_path
    where f.submission_id = p_submission_id
      and o.owner_id = v_profile.id::text
  ) then raise exception 'ARCHIVE_OBJECT_NOT_FOUND' using errcode = 'P0002'; end if;

  v_receipt := upper(substr(encode(digest(p_submission_id::text || ':' || v_received::text, 'sha256'), 'hex'), 1, 16));
  update public.submissions
  set status = case when v_received > deadline_at then 'LateSubmitted' else 'Submitted' end,
      server_received_at = v_received,
      is_late = v_received > deadline_at,
      is_official = true,
      receipt_code = v_receipt,
      receipt_signature = encode(digest(id::text || ':' || v_receipt, 'sha256'), 'hex'),
      cloud_version = private.next_public_cloud_version(),
      updated_at = v_received
  where id = p_submission_id;
  update public.session_participants
  set submission_status = case when v_received > v_submission.deadline_at then 'LateSubmitted' else 'Submitted' end,
      cloud_version = private.next_public_cloud_version(), updated_at = v_received
  where id = v_submission.participant_id;
  return v_receipt;
end
$function$;

create or replace function public.upsert_public_device_heartbeat(
  p_session_id uuid,
  p_device_id text,
  p_connection_state text,
  p_foreground_application text default null,
  p_running_process_summary jsonb default '[]'::jsonb,
  p_app_version text default null,
  p_agent_version text default null)
returns uuid
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
  v_participant public.session_participants%rowtype;
  v_connection_id uuid;
begin
  if length(btrim(coalesce(p_device_id, ''))) not between 1 and 128 then
    raise exception 'DEVICE_ID_INVALID' using errcode = '22023';
  end if;
  if p_connection_state not in ('Offline','Connecting','Online','Reconnecting','Degraded') then
    raise exception 'CONNECTION_STATE_INVALID' using errcode = '22023';
  end if;
  if pg_column_size(coalesce(p_running_process_summary, '[]'::jsonb)) > 65536 then
    raise exception 'HEARTBEAT_PAYLOAD_TOO_LARGE' using errcode = '22023';
  end if;
  select p.* into v_participant
  from public.session_participants p
  join public.exam_sessions s
    on s.id = p.session_id and s.organization_id = p.organization_id
  join public.class_members m
    on m.class_id = s.class_id and m.user_id = p.user_id
   and m.organization_id = p.organization_id
  where p.session_id = p_session_id
    and p.user_id = v_profile.id
    and p.organization_id = v_profile.organization_id
    and p.status in ('Approved','Disconnected')
    and p.source_mode = 'PublicCloud'
    and s.access_mode = 'PublicCloud'
    and s.status in ('Waiting','Distributing','InProgress','Paused','Collecting');
  if not found then raise exception 'PUBLIC_PARTICIPANT_NOT_ACTIVE' using errcode = '42501'; end if;

  insert into public.public_device_connections(
    organization_id, session_id, participant_id, user_id, device_id,
    connection_state, heartbeat_at, foreground_application,
    running_process_summary, app_version, agent_version, source_mode,
    cloud_version, created_at, updated_at)
  values (
    v_profile.organization_id, p_session_id, v_participant.id, v_profile.id,
    btrim(p_device_id), p_connection_state, now(),
    left(nullif(btrim(p_foreground_application), ''), 512),
    coalesce(p_running_process_summary, '[]'::jsonb),
    left(nullif(btrim(p_app_version), ''), 64),
    left(nullif(btrim(p_agent_version), ''), 64),
    'PublicCloud', private.next_public_cloud_version(), now(), now())
  on conflict (session_id, device_id) do update
    set connection_state = excluded.connection_state,
        heartbeat_at = now(),
        foreground_application = excluded.foreground_application,
        running_process_summary = excluded.running_process_summary,
        app_version = excluded.app_version,
        agent_version = excluded.agent_version,
        cloud_version = private.next_public_cloud_version(),
        updated_at = now()
    where public.public_device_connections.user_id = v_profile.id
      and public.public_device_connections.participant_id = v_participant.id
  returning id into v_connection_id;
  if v_connection_id is null then raise exception 'DEVICE_OWNERSHIP_MISMATCH' using errcode = '42501'; end if;

  update public.session_participants
  set last_seen_at = now(), device_id = btrim(p_device_id), app_version = left(nullif(btrim(p_app_version), ''), 64),
      cloud_version = private.next_public_cloud_version(), updated_at = now()
  where id = v_participant.id;
  return v_connection_id;
end
$function$;

create or replace function public.report_public_violation(
  p_session_id uuid,
  p_device_id text,
  p_violation_type text,
  p_evidence_metadata jsonb default '{}'::jsonb)
returns uuid
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
  v_connection public.public_device_connections%rowtype;
  v_class_id uuid;
  v_id uuid := gen_random_uuid();
begin
  if length(btrim(coalesce(p_violation_type, ''))) not between 1 and 128 then
    raise exception 'VIOLATION_TYPE_INVALID' using errcode = '22023';
  end if;
  if pg_column_size(coalesce(p_evidence_metadata, '{}'::jsonb)) > 65536 then
    raise exception 'VIOLATION_EVIDENCE_TOO_LARGE' using errcode = '22023';
  end if;
  select c, s.class_id into v_connection, v_class_id
  from public.public_device_connections c
  join public.exam_sessions s
    on s.id = c.session_id and s.organization_id = c.organization_id
  join public.session_participants p
    on p.id = c.participant_id and p.session_id = c.session_id
   and p.organization_id = c.organization_id and p.user_id = c.user_id
  join public.class_members m
    on m.class_id = s.class_id and m.user_id = c.user_id
   and m.organization_id = c.organization_id
  where c.session_id = p_session_id
    and c.device_id = btrim(p_device_id)
    and c.user_id = v_profile.id
    and c.organization_id = v_profile.organization_id
    and s.access_mode = 'PublicCloud'
    and s.status in ('Waiting','Distributing','InProgress','Paused','Collecting');
  if not found then raise exception 'DEVICE_CONNECTION_NOT_FOUND' using errcode = 'P0002'; end if;

  insert into public.violations(
    id, organization_id, class_id, session_id, participant_id, device_id,
    type, severity, occurred_at, payload_json, evidence_metadata, status,
    source_mode, cloud_version, created_at, updated_at)
  values (
    v_id, v_profile.organization_id, v_class_id, p_session_id,
    v_connection.participant_id, v_connection.device_id,
    btrim(p_violation_type),
    case when lower(p_violation_type) in ('tamper','agentstopped','processterminated') then 'High' else 'Warning' end,
    now(), '{}'::jsonb, coalesce(p_evidence_metadata, '{}'::jsonb), 'Open',
    'PublicCloud', private.next_public_cloud_version(), now(), now());

  update public.public_device_connections
  set violation_count = violation_count + 1,
      cloud_version = private.next_public_cloud_version(), updated_at = now()
  where id = v_connection.id;
  return v_id;
end
$function$;

create or replace function public.ack_public_device_command(
  p_command_id uuid,
  p_device_id text,
  p_status text,
  p_error_code text default null,
  p_error_message text default null)
returns text
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
  v_command public.public_device_commands%rowtype;
  v_existing public.public_device_command_results%rowtype;
  v_now timestamptz := now();
begin
  if p_status not in ('Received','Executed','Failed','Expired','Ignored') then
    raise exception 'COMMAND_RESULT_STATUS_INVALID' using errcode = '22023';
  end if;
  select d.* into v_command
  from public.public_device_commands d
  join public.public_device_connections c
    on c.session_id = d.session_id and c.device_id = d.device_id
   and c.organization_id = d.organization_id
  join public.exam_sessions s
    on s.id = d.session_id and s.organization_id = d.organization_id
   and s.access_mode = 'PublicCloud'
  join public.class_members m
    on m.class_id = s.class_id and m.user_id = c.user_id
   and m.organization_id = c.organization_id
  where d.command_id = p_command_id
    and d.device_id = btrim(p_device_id)
    and c.user_id = v_profile.id
    and d.organization_id = v_profile.organization_id;
  if not found then raise exception 'DEVICE_COMMAND_NOT_FOUND' using errcode = 'P0002'; end if;
  if v_command.expires_at <= v_now and p_status not in ('Expired','Ignored') then
    raise exception 'DEVICE_COMMAND_EXPIRED' using errcode = '55000';
  end if;

  select * into v_existing
  from public.public_device_command_results
  where command_id = p_command_id
  for update;
  if found then
    if v_existing.status in ('Executed','Failed','Expired','Ignored') then
      if v_existing.status = p_status then return v_existing.status; end if;
      raise exception 'COMMAND_RESULT_FINAL' using errcode = '55000';
    end if;
    if v_existing.status <> 'Received' or p_status = 'Received' then
      return v_existing.status;
    end if;
    update public.public_device_command_results
    set status = p_status,
        executed_at = v_now,
        error_code = case when p_status = 'Failed' then left(nullif(p_error_code, ''), 128) else null end,
        error_message = case when p_status = 'Failed' then left(nullif(p_error_message, ''), 2000) else null end,
        cloud_version = private.next_public_cloud_version(),
        updated_at = v_now
    where command_id = p_command_id;
    return p_status;
  end if;

  insert into public.public_device_command_results(
    command_id, organization_id, device_id, status, received_at, executed_at,
    error_code, error_message, source_mode, cloud_version, created_at, updated_at)
  values (
    p_command_id, v_command.organization_id, v_command.device_id, p_status, v_now,
    case when p_status = 'Received' then null else v_now end,
    case when p_status = 'Failed' then left(nullif(p_error_code, ''), 128) else null end,
    case when p_status = 'Failed' then left(nullif(p_error_message, ''), 2000) else null end,
    'PublicCloud', private.next_public_cloud_version(), v_now, v_now);
  return p_status;
end
$function$;

create or replace function public.start_public_quiz_attempt(
  p_session_id uuid,
  p_idempotency_key text)
returns uuid
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
  v_session public.exam_sessions%rowtype;
  v_participant public.session_participants%rowtype;
  v_exam public.exams%rowtype;
  v_attempt_id uuid;
  v_deadline timestamptz;
  v_snapshot jsonb;
  v_max_score numeric(10,2);
begin
  if length(btrim(coalesce(p_idempotency_key, ''))) not between 8 and 128 then
    raise exception 'IDEMPOTENCY_KEY_INVALID' using errcode = '22023';
  end if;
  perform pg_advisory_xact_lock(hashtextextended(p_session_id::text || ':quiz:' || v_profile.id::text, 0));
  select s.* into v_session
  from public.exam_sessions s
  where s.id = p_session_id
    and s.organization_id = v_profile.organization_id
    and s.access_mode = 'PublicCloud'
    and s.status in ('InProgress','Paused');
  if not found then raise exception 'PUBLIC_QUIZ_SESSION_NOT_ACTIVE' using errcode = 'P0002'; end if;
  select * into v_exam from public.exams
  where id = v_session.exam_id
    and organization_id = v_profile.organization_id
    and class_id = v_session.class_id
    and delivery_type = 'MultipleChoice';
  if not found then raise exception 'PUBLIC_QUIZ_NOT_FOUND' using errcode = 'P0002'; end if;
  if not exists (
    select 1 from public.class_members m
    where m.class_id = v_session.class_id
      and m.organization_id = v_profile.organization_id
      and m.user_id = v_profile.id
  ) then raise exception 'CLASS_MEMBERSHIP_REQUIRED' using errcode = '42501'; end if;
  if not exists (
    select 1 from public.public_class_assignments a
    where a.organization_id = v_profile.organization_id
      and a.class_id = v_session.class_id and a.exam_id = v_session.exam_id
      and (a.available_from is null or a.available_from <= now())
      and (a.available_until is null or a.available_until >= now())
  ) then raise exception 'PUBLIC_ASSIGNMENT_UNAVAILABLE' using errcode = '42501'; end if;
  select * into v_participant
  from public.session_participants
  where session_id = p_session_id and user_id = v_profile.id
    and organization_id = v_profile.organization_id
    and status = 'Approved' and source_mode = 'PublicCloud';
  if not found then raise exception 'APPROVED_PARTICIPANT_REQUIRED' using errcode = '42501'; end if;

  select id into v_attempt_id from public.quiz_attempts
  where session_id = p_session_id and participant_id = v_participant.id;
  if found then return v_attempt_id; end if;
  if v_session.started_at is null then raise exception 'SESSION_NOT_STARTED' using errcode = '55000'; end if;
  v_deadline := v_session.started_at
    + make_interval(mins => v_exam.duration_minutes + greatest(v_participant.extra_time_minutes, 0));

  select coalesce(sum(q.points), 0),
         coalesce(jsonb_agg(jsonb_build_object(
           'id', q.id, 'sortOrder', q.sort_order, 'questionText', q.question_text,
           'points', q.points, 'multiple', q.multiple,
           'choices', (select coalesce(jsonb_agg(jsonb_build_object(
             'id', c.id, 'sortOrder', c.sort_order, 'choiceText', c.choice_text)
             order by c.sort_order), '[]'::jsonb)
             from public.quiz_choices c where c.question_id = q.id))
           order by q.sort_order), '[]'::jsonb)
  into v_max_score, v_snapshot
  from public.quiz_questions q
  where q.exam_id = v_exam.id and q.version = v_exam.version
    and q.organization_id = v_profile.organization_id;
  if v_max_score <= 0 then raise exception 'QUIZ_HAS_NO_QUESTIONS' using errcode = '55000'; end if;

  v_attempt_id := gen_random_uuid();
  insert into public.quiz_attempts(
    id, organization_id, session_id, participant_id, exam_version, status,
    started_at, deadline_at, max_score, snapshot_json,
    finalize_idempotency_key, source_mode, cloud_version, created_at, updated_at)
  values (
    v_attempt_id, v_profile.organization_id, p_session_id, v_participant.id,
    v_exam.version, 'InProgress', now(), v_deadline, v_max_score, v_snapshot,
    null, 'PublicCloud', private.next_public_cloud_version(), now(), now());
  return v_attempt_id;
end
$function$;

create or replace function public.save_public_quiz_answers(
  p_attempt_id uuid,
  p_question_id uuid,
  p_choice_ids jsonb,
  p_revision bigint,
  p_client_updated_at timestamptz)
returns bigint
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
  v_attempt public.quiz_attempts%rowtype;
  v_question public.quiz_questions%rowtype;
  v_existing_revision bigint;
  v_count integer;
begin
  if p_revision <= 0 or p_choice_ids is null or jsonb_typeof(p_choice_ids) <> 'array' then
    raise exception 'QUIZ_ANSWER_INVALID' using errcode = '22023';
  end if;
  if jsonb_array_length(p_choice_ids) > 100 or pg_column_size(p_choice_ids) > 16384 then
    raise exception 'QUIZ_ANSWER_TOO_LARGE' using errcode = '22023';
  end if;
  select a.* into v_attempt
  from public.quiz_attempts a
  join public.session_participants p on p.id = a.participant_id
  join public.exam_sessions s on s.id = a.session_id
  join public.class_members m
    on m.class_id = s.class_id and m.user_id = p.user_id
   and m.organization_id = s.organization_id
  where a.id = p_attempt_id
    and a.organization_id = v_profile.organization_id
    and a.source_mode = 'PublicCloud'
    and p.user_id = v_profile.id
    and p.organization_id = a.organization_id
    and s.access_mode = 'PublicCloud'
    and s.status in ('InProgress','Paused');
  if not found then raise exception 'PUBLIC_QUIZ_ATTEMPT_NOT_FOUND' using errcode = 'P0002'; end if;
  if v_attempt.status <> 'InProgress' or now() > v_attempt.deadline_at then
    raise exception 'QUIZ_ATTEMPT_CLOSED' using errcode = '55000';
  end if;
  select q.* into v_question
  from public.quiz_questions q
  join public.exam_sessions s on s.id = v_attempt.session_id
  where q.id = p_question_id
    and q.exam_id = s.exam_id
    and q.version = v_attempt.exam_version
    and q.organization_id = v_profile.organization_id;
  if not found then raise exception 'QUIZ_QUESTION_NOT_FOUND' using errcode = 'P0002'; end if;
  select count(*) into v_count from jsonb_array_elements_text(p_choice_ids);
  if (not v_question.multiple and v_count > 1) or v_count = 0 then
    raise exception 'QUIZ_CHOICE_COUNT_INVALID' using errcode = '22023';
  end if;
  if exists (
    select 1 from jsonb_array_elements_text(p_choice_ids) x(value)
    where not case
      when value ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$'
      then exists (
        select 1 from public.quiz_choices c
        where c.id = value::uuid and c.question_id = p_question_id
          and c.organization_id = v_profile.organization_id)
      else false
    end
  ) then raise exception 'QUIZ_CHOICE_INVALID' using errcode = '22023'; end if;

  select revision into v_existing_revision
  from public.quiz_answers
  where attempt_id = p_attempt_id and question_id = p_question_id
  for update;
  if found and p_revision <= v_existing_revision then return v_existing_revision; end if;
  insert into public.quiz_answers(
    id, organization_id, attempt_id, question_id, choice_ids, revision,
    client_updated_at, source_mode, cloud_version, created_at, updated_at)
  values (
    gen_random_uuid(), v_profile.organization_id, p_attempt_id, p_question_id,
    p_choice_ids, p_revision, least(coalesce(p_client_updated_at, now()), now()),
    'PublicCloud', private.next_public_cloud_version(), now(), now())
  on conflict (attempt_id, question_id) do update
    set choice_ids = excluded.choice_ids,
        revision = excluded.revision,
        client_updated_at = excluded.client_updated_at,
        cloud_version = private.next_public_cloud_version(),
        updated_at = now()
    where excluded.revision > public.quiz_answers.revision;
  return greatest(p_revision, coalesce(v_existing_revision, 0));
end
$function$;

create or replace function public.finalize_public_quiz_attempt(
  p_attempt_id uuid,
  p_idempotency_key text)
returns numeric
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
  v_attempt public.quiz_attempts%rowtype;
  v_score numeric(10,2) := 0;
  v_row record;
begin
  if length(btrim(coalesce(p_idempotency_key, ''))) not between 8 and 128 then
    raise exception 'IDEMPOTENCY_KEY_INVALID' using errcode = '22023';
  end if;
  perform pg_advisory_xact_lock(hashtextextended(p_attempt_id::text, 0));
  select a.* into v_attempt
  from public.quiz_attempts a
  join public.session_participants p on p.id = a.participant_id
  join public.exam_sessions s on s.id = a.session_id
  join public.class_members m
    on m.class_id = s.class_id and m.user_id = p.user_id
   and m.organization_id = s.organization_id
  where a.id = p_attempt_id
    and a.organization_id = v_profile.organization_id
    and a.source_mode = 'PublicCloud'
    and p.user_id = v_profile.id
    and p.organization_id = a.organization_id
    and s.access_mode = 'PublicCloud';
  if not found then raise exception 'PUBLIC_QUIZ_ATTEMPT_NOT_FOUND' using errcode = 'P0002'; end if;
  if v_attempt.status = 'Finalized' then
    if v_attempt.finalize_idempotency_key = btrim(p_idempotency_key) then return v_attempt.score; end if;
    raise exception 'QUIZ_ATTEMPT_ALREADY_FINALIZED' using errcode = '55000';
  end if;
  if v_attempt.status <> 'InProgress' then raise exception 'QUIZ_ATTEMPT_CLOSED' using errcode = '55000'; end if;

  for v_row in
    select q.id, q.points,
      coalesce((select array_agg(c.id order by c.id) from public.quiz_choices c
                where c.question_id = q.id and c.is_correct), array[]::uuid[]) as correct_ids,
      coalesce((select array_agg(x.value::uuid order by x.value::uuid)
                from public.quiz_answers a,
                     lateral jsonb_array_elements_text(a.choice_ids) x(value)
                where a.attempt_id = p_attempt_id and a.question_id = q.id), array[]::uuid[]) as selected_ids
    from public.quiz_questions q
    join public.exam_sessions s on s.id = v_attempt.session_id
    where q.exam_id = s.exam_id and q.version = v_attempt.exam_version
      and q.organization_id = v_profile.organization_id
  loop
    if v_row.correct_ids = v_row.selected_ids then v_score := v_score + v_row.points; end if;
  end loop;

  update public.quiz_attempts
  set status = 'Finalized', finalized_at = now(), score = v_score,
      finalize_idempotency_key = btrim(p_idempotency_key),
      cloud_version = private.next_public_cloud_version(), updated_at = now()
  where id = p_attempt_id;
  return v_score;
end
$function$;

-- Called only by the Edge Function with a service-role credential. The desktop
-- teacher client never receives the HMAC key and cannot insert commands.
create or replace function public.issue_public_device_command(
  p_command_id uuid,
  p_session_id uuid,
  p_device_id text,
  p_command_type text,
  p_payload jsonb,
  p_created_at timestamptz,
  p_expires_at timestamptz,
  p_issued_by uuid,
  p_signature text)
returns uuid
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype;
begin
  select * into v_profile from public.profiles
  where id = p_issued_by and is_active = true and role in ('Admin','Teacher');
  if not found then raise exception 'STAFF_REQUIRED' using errcode = '42501'; end if;
  if p_command_type not in ('ApplyPolicy','UpdatePolicy','ShowWarning','LockExamApplication','UnlockExamApplication','ForceFocusExamApplication','RequestDeviceSnapshot','RequestRunningProcesses','ForceSubmit','EndDeviceSession','ClearPolicy') then
    raise exception 'COMMAND_TYPE_INVALID' using errcode = '22023';
  end if;
  if pg_column_size(coalesce(p_payload, '{}'::jsonb)) > 65536 then
    raise exception 'COMMAND_PAYLOAD_TOO_LARGE' using errcode = '22023';
  end if;
  if p_created_at < now() - interval '1 minute' or p_created_at > now() + interval '1 minute'
     or p_expires_at <= p_created_at or p_expires_at > p_created_at + interval '15 minutes' then
    raise exception 'COMMAND_EXPIRY_INVALID' using errcode = '22023';
  end if;
  if lower(coalesce(p_signature, '')) !~ '^[0-9a-f]{64}$' then
    raise exception 'COMMAND_SIGNATURE_INVALID' using errcode = '22023';
  end if;
  if not exists (
    select 1
    from public.public_device_connections c
    join public.exam_sessions s
      on s.id = c.session_id and s.organization_id = c.organization_id
    join public.exams e
      on e.id = s.exam_id and e.organization_id = s.organization_id
    where c.session_id = p_session_id and c.device_id = btrim(p_device_id)
      and c.organization_id = v_profile.organization_id
      and s.access_mode = 'PublicCloud'
      and (v_profile.role = 'Admin' or e.created_by = p_issued_by)
  ) then raise exception 'DEVICE_SESSION_FORBIDDEN' using errcode = '42501'; end if;

  insert into public.public_device_commands(
    command_id, organization_id, session_id, device_id, command_type,
    payload, created_at, expires_at, issued_by, signature)
  values (
    p_command_id, v_profile.organization_id, p_session_id, btrim(p_device_id),
    p_command_type, coalesce(p_payload, '{}'::jsonb), p_created_at, p_expires_at,
    p_issued_by, lower(p_signature));
  return p_command_id;
end
$function$;

create table if not exists public.cloud_sync_cursors (
  consumer_id text primary key,
  source_mode text not null check (source_mode in ('Lan','PublicCloud')),
  last_cloud_version bigint not null default 0 check (last_cloud_version >= 0),
  last_entity_id uuid,
  updated_at timestamptz not null default now()
);
alter table public.cloud_sync_cursors enable row level security;
revoke all on public.cloud_sync_cursors from public, anon, authenticated;
grant select, insert, update on public.cloud_sync_cursors to service_role;

create or replace function private.enforce_public_tenant_consistency()
returns trigger
language plpgsql
security definer
set search_path = ''
as $function$
begin
  if tg_table_name = 'exam_sessions' and new.access_mode = 'PublicCloud' and not exists (
    select 1 from public.exams e join public.classes c on c.id = new.class_id
    where e.id = new.exam_id and e.organization_id = new.organization_id
      and c.organization_id = new.organization_id and e.class_id = new.class_id
  ) then raise exception 'SESSION_TENANT_MISMATCH' using errcode = '23514';
  elsif tg_table_name = 'session_participants' and new.source_mode = 'PublicCloud' and not exists (
    select 1 from public.exam_sessions s
    where s.id = new.session_id and s.organization_id = new.organization_id
      and s.access_mode = 'PublicCloud'
  ) then raise exception 'PARTICIPANT_TENANT_MISMATCH' using errcode = '23514';
  elsif tg_table_name = 'public_class_assignments' and not exists (
    select 1 from public.classes c join public.exams e on e.id = new.exam_id
    where c.id = new.class_id and c.organization_id = new.organization_id
      and e.organization_id = new.organization_id and e.class_id = new.class_id
  ) then raise exception 'ASSIGNMENT_TENANT_MISMATCH' using errcode = '23514';
  elsif tg_table_name = 'public_device_connections' and not exists (
    select 1 from public.exam_sessions s
    join public.session_participants p on p.session_id = s.id
    where s.id = new.session_id and p.id = new.participant_id
      and s.organization_id = new.organization_id
      and p.organization_id = new.organization_id
      and p.user_id = new.user_id and s.access_mode = 'PublicCloud'
  ) then raise exception 'DEVICE_TENANT_MISMATCH' using errcode = '23514';
  elsif tg_table_name = 'public_device_commands' and not exists (
    select 1 from public.public_device_connections c
    where c.session_id = new.session_id and c.device_id = new.device_id
      and c.organization_id = new.organization_id
  ) then raise exception 'COMMAND_DEVICE_MISMATCH' using errcode = '23514';
  elsif tg_table_name = 'public_device_command_results' and not exists (
    select 1 from public.public_device_commands c
    where c.command_id = new.command_id and c.device_id = new.device_id
      and c.organization_id = new.organization_id
  ) then raise exception 'COMMAND_RESULT_MISMATCH' using errcode = '23514';
  elsif tg_table_name = 'violations' and new.source_mode = 'PublicCloud' and not exists (
    select 1 from public.session_participants p
    join public.exam_sessions s on s.id = p.session_id
    where p.id = new.participant_id and p.session_id = new.session_id
      and p.organization_id = new.organization_id
      and s.organization_id = new.organization_id
      and s.class_id is not distinct from new.class_id
      and s.access_mode = 'PublicCloud'
  ) then raise exception 'VIOLATION_TENANT_MISMATCH' using errcode = '23514';
  elsif tg_table_name = 'submissions' and new.source_mode = 'PublicCloud' and not exists (
    select 1 from public.session_participants p
    join public.exam_sessions s on s.id = p.session_id
    where p.id = new.participant_id and p.session_id = new.session_id
      and p.organization_id = new.organization_id
      and s.organization_id = new.organization_id and s.access_mode = 'PublicCloud'
  ) then raise exception 'SUBMISSION_TENANT_MISMATCH' using errcode = '23514';
  elsif tg_table_name = 'submission_files' and new.source_mode = 'PublicCloud' and not exists (
    select 1 from public.submissions s
    where s.id = new.submission_id and s.organization_id = new.organization_id
      and s.source_mode = 'PublicCloud'
  ) then raise exception 'SUBMISSION_FILE_TENANT_MISMATCH' using errcode = '23514';
  elsif tg_table_name = 'quiz_attempts' and new.source_mode = 'PublicCloud' and not exists (
    select 1 from public.session_participants p
    join public.exam_sessions s on s.id = p.session_id
    where p.id = new.participant_id and p.session_id = new.session_id
      and p.organization_id = new.organization_id
      and s.organization_id = new.organization_id and s.access_mode = 'PublicCloud'
  ) then raise exception 'QUIZ_ATTEMPT_TENANT_MISMATCH' using errcode = '23514';
  elsif tg_table_name = 'quiz_answers' and new.source_mode = 'PublicCloud' and not exists (
    select 1 from public.quiz_attempts a
    join public.quiz_questions q on q.id = new.question_id
    where a.id = new.attempt_id and a.organization_id = new.organization_id
      and q.organization_id = new.organization_id and a.source_mode = 'PublicCloud'
  ) then raise exception 'QUIZ_ANSWER_TENANT_MISMATCH' using errcode = '23514';
  end if;
  return new;
end
$function$;
revoke all on function private.enforce_public_tenant_consistency() from public, anon, authenticated, service_role;

do $triggers$
declare v_table text;
begin
  foreach v_table in array array[
    'exam_sessions','session_participants','public_class_assignments','public_device_connections','public_device_commands',
    'public_device_command_results','violations','submissions','submission_files',
    'quiz_attempts','quiz_answers'
  ] loop
    execute format('drop trigger if exists trg_public_tenant_consistency on public.%I', v_table);
    execute format(
      'create trigger trg_public_tenant_consistency before insert or update on public.%I for each row execute function private.enforce_public_tenant_consistency()',
      v_table);
  end loop;
end
$triggers$;

-- Function execution is deny-by-default. Only the narrow student RPCs are
-- exposed to authenticated users; command issuance remains service-role only.
revoke all on function public.join_public_session(uuid,text,text,text,jsonb) from public, anon, authenticated;
revoke all on function public.init_public_submission(uuid,text,text,bigint,text) from public, anon, authenticated;
revoke all on function public.finalize_public_submission(uuid,text) from public, anon, authenticated;
revoke all on function public.upsert_public_device_heartbeat(uuid,text,text,text,jsonb,text,text) from public, anon, authenticated;
revoke all on function public.report_public_violation(uuid,text,text,jsonb) from public, anon, authenticated;
revoke all on function public.ack_public_device_command(uuid,text,text,text,text) from public, anon, authenticated;
revoke all on function public.start_public_quiz_attempt(uuid,text) from public, anon, authenticated;
revoke all on function public.save_public_quiz_answers(uuid,uuid,jsonb,bigint,timestamptz) from public, anon, authenticated;
revoke all on function public.finalize_public_quiz_attempt(uuid,text) from public, anon, authenticated;
revoke all on function public.issue_public_device_command(uuid,uuid,text,text,jsonb,timestamptz,timestamptz,uuid,text) from public, anon, authenticated;
grant execute on function public.join_public_session(uuid,text,text,text,jsonb) to authenticated;
grant execute on function public.init_public_submission(uuid,text,text,bigint,text) to authenticated;
grant execute on function public.finalize_public_submission(uuid,text) to authenticated;
grant execute on function public.upsert_public_device_heartbeat(uuid,text,text,text,jsonb,text,text) to authenticated;
grant execute on function public.report_public_violation(uuid,text,text,jsonb) to authenticated;
grant execute on function public.ack_public_device_command(uuid,text,text,text,text) to authenticated;
grant execute on function public.start_public_quiz_attempt(uuid,text) to authenticated;
grant execute on function public.save_public_quiz_answers(uuid,uuid,jsonb,bigint,timestamptz) to authenticated;
grant execute on function public.finalize_public_quiz_attempt(uuid,text) to authenticated;
grant execute on function public.issue_public_device_command(uuid,uuid,text,text,jsonb,timestamptz,timestamptz,uuid,text) to service_role;

drop policy if exists classes_tenant_select on public.classes;
create policy classes_public_or_authorized_select on public.classes for select to authenticated using (
  organization_id = (select public.current_organization_id()) and (
    (select public.current_examtransfer_role()) in ('Admin','Teacher') or access_mode = 'Public'
    or exists (select 1 from public.class_members m where m.class_id = classes.id and m.user_id = (select auth.uid()))));

drop policy if exists class_members_tenant_select on public.class_members;
create policy class_members_staff_or_self_select on public.class_members for select to authenticated using (
  organization_id = (select public.current_organization_id()) and (
    (select public.current_examtransfer_role()) in ('Admin','Teacher') or user_id = (select auth.uid())));

drop policy if exists exam_sessions_tenant_select on public.exam_sessions;
create policy sessions_staff_or_public_member_select on public.exam_sessions for select to authenticated using (
  organization_id = (select public.current_organization_id()) and (
    (select public.current_examtransfer_role()) in ('Admin','Teacher')
    or exists (select 1 from public.class_members m where m.class_id = exam_sessions.class_id and m.user_id = (select auth.uid()))
    or exists (select 1 from public.session_participants p where p.session_id = exam_sessions.id and p.user_id = (select auth.uid()))));

drop policy if exists exams_tenant_select on public.exams;
create policy exams_staff_or_assigned_student_select on public.exams for select to authenticated using (
  organization_id = (select public.current_organization_id()) and (
    (select public.current_examtransfer_role()) in ('Admin','Teacher')
    or exists (select 1 from public.public_class_assignments a join public.class_members m on m.class_id = a.class_id where a.exam_id = exams.id and m.user_id = (select auth.uid()))));

drop policy if exists exam_files_tenant_select on public.exam_files;
create policy exam_files_staff_or_assigned_student_select on public.exam_files for select to authenticated using (
  organization_id = (select public.current_organization_id()) and (
    (select public.current_examtransfer_role()) in ('Admin','Teacher')
    or exists (select 1 from public.public_class_assignments a join public.class_members m on m.class_id = a.class_id where a.exam_id = exam_files.exam_id and m.user_id = (select auth.uid()))));

drop policy if exists session_participants_tenant_select on public.session_participants;
create policy participants_staff_or_self_select on public.session_participants for select to authenticated using (
  organization_id = (select public.current_organization_id()) and (
    (select public.current_examtransfer_role()) in ('Admin','Teacher') or user_id = (select auth.uid())));
drop policy if exists participants_public_owner_insert on public.session_participants;
drop policy if exists participants_public_owner_update on public.session_participants;

drop policy if exists submissions_tenant_select on public.submissions;
create policy submissions_staff_or_owner_select on public.submissions for select to authenticated using (
  organization_id = (select public.current_organization_id()) and (
    (select public.current_examtransfer_role()) in ('Admin','Teacher')
    or exists (select 1 from public.session_participants p where p.id = submissions.participant_id and p.user_id = (select auth.uid()))));
drop policy if exists submissions_public_owner_insert on public.submissions;
drop policy if exists submissions_public_owner_update on public.submissions;

drop policy if exists submission_files_tenant_select on public.submission_files;
create policy submission_files_staff_or_owner_select on public.submission_files for select to authenticated using (
  organization_id = (select public.current_organization_id()) and (
    (select public.current_examtransfer_role()) in ('Admin','Teacher')
    or exists (select 1 from public.submissions s join public.session_participants p on p.id = s.participant_id where s.id = submission_files.submission_id and p.user_id = (select auth.uid()))));
drop policy if exists submission_files_public_owner_insert on public.submission_files;
drop policy if exists submission_files_public_owner_update on public.submission_files;

drop policy if exists grades_tenant_select on public.grades;
create policy grades_staff_or_returned_owner_select on public.grades for select to authenticated using (
  organization_id = (select public.current_organization_id()) and (
    (select public.current_examtransfer_role()) in ('Admin','Teacher')
    or (status = 'Returned' and exists (select 1 from public.submissions s join public.session_participants p on p.id = s.participant_id where s.id = grades.submission_id and p.user_id = (select auth.uid())))));

drop policy if exists control_policies_tenant_select on public.control_policies;
create policy control_policies_staff_or_participant_select on public.control_policies for select to authenticated using (
  organization_id = (select public.current_organization_id()) and (
    (select public.current_examtransfer_role()) in ('Admin','Teacher')
    or exists (select 1 from public.session_participants p where p.session_id = control_policies.session_id and p.user_id = (select auth.uid()))));

drop policy if exists violations_tenant_select on public.violations;
create policy violations_staff_or_owner_select on public.violations for select to authenticated using (
  organization_id = (select public.current_organization_id()) and (
    (select public.current_examtransfer_role()) in ('Admin','Teacher')
    or exists (select 1 from public.session_participants p where p.id = violations.participant_id and p.user_id = (select auth.uid()))));
drop policy if exists violations_public_owner_insert on public.violations;

create policy enrollment_staff_or_self_select on public.class_enrollment_requests for select to authenticated using (
  organization_id = (select public.current_organization_id()) and ((select public.current_examtransfer_role()) in ('Admin','Teacher') or student_user_id = (select auth.uid())));
create policy enrollment_staff_update on public.class_enrollment_requests for update to authenticated
  using (organization_id = (select public.current_organization_id()) and (select public.current_examtransfer_role()) in ('Admin','Teacher'))
  with check (organization_id = (select public.current_organization_id()) and (select public.current_examtransfer_role()) in ('Admin','Teacher'));
create policy assignments_staff_or_member_select on public.public_class_assignments for select to authenticated using (
  organization_id = (select public.current_organization_id()) and ((select public.current_examtransfer_role()) in ('Admin','Teacher') or exists (select 1 from public.class_members m where m.class_id = public_class_assignments.class_id and m.user_id = (select auth.uid()))));
create policy assignments_staff_write on public.public_class_assignments for all to authenticated
  using (organization_id = (select public.current_organization_id()) and (select public.current_examtransfer_role()) in ('Admin','Teacher'))
  with check (organization_id = (select public.current_organization_id()) and (select public.current_examtransfer_role()) in ('Admin','Teacher'));

create policy device_connections_staff_or_owner_select on public.public_device_connections for select to authenticated using (
  organization_id = (select public.current_organization_id()) and ((select public.current_examtransfer_role()) in ('Admin','Teacher') or user_id = (select auth.uid())));
drop policy if exists device_connections_owner_insert on public.public_device_connections;
drop policy if exists device_connections_owner_update on public.public_device_connections;

drop policy if exists device_commands_staff_insert on public.public_device_commands;
drop policy if exists device_commands_staff_update on public.public_device_commands;
create policy device_commands_staff_or_device_select on public.public_device_commands for select to authenticated using (
  organization_id = (select public.current_organization_id()) and ((select public.current_examtransfer_role()) in ('Admin','Teacher') or exists (select 1 from public.public_device_connections c where c.session_id = public_device_commands.session_id and c.device_id = public_device_commands.device_id and c.user_id = (select auth.uid()))));
create policy command_results_staff_or_device_select on public.public_device_command_results for select to authenticated using (
  organization_id = (select public.current_organization_id()) and ((select public.current_examtransfer_role()) in ('Admin','Teacher') or exists (select 1 from public.public_device_connections c join public.public_device_commands d on d.session_id = c.session_id and d.device_id = c.device_id where d.command_id = public_device_command_results.command_id and c.user_id = (select auth.uid()))));
drop policy if exists command_results_device_insert on public.public_device_command_results;

drop policy if exists examtransfer_storage_select on storage.objects;
create policy examtransfer_storage_staff_select on storage.objects for select to authenticated using (
  bucket_id in ('exam-archives','submission-archives','public-submission-archives','report-exports','backup-archives')
  and (storage.foldername(name))[1] = (select public.current_organization_id())::text
  and (select public.current_examtransfer_role()) in ('Admin','Teacher'));
insert into storage.buckets(id, name, public, file_size_limit)
values ('public-submission-archives', 'public-submission-archives', false, 10485760)
on conflict (id) do update
set public = false,
    file_size_limit = least(coalesce(storage.buckets.file_size_limit, 10485760), 10485760);
drop policy if exists examtransfer_public_submission_owner_select on storage.objects;
drop policy if exists examtransfer_public_submission_owner_insert on storage.objects;
drop policy if exists examtransfer_public_submission_owner_update on storage.objects;
create policy examtransfer_public_submission_owner_select on storage.objects for select to authenticated using (
  bucket_id = 'public-submission-archives'
  and owner_id = (select auth.uid())::text
  and (storage.foldername(name))[1] = (select public.current_organization_id())::text
  and (storage.foldername(name))[2] = 'public-submissions'
  and (storage.foldername(name))[3] = (select auth.uid())::text
  and array_length(storage.foldername(name), 1) = 4
  and exists (
    select 1 from public.submissions s
    join public.session_participants p on p.id = s.participant_id
    where s.id::text = (storage.foldername(name))[4]
      and s.organization_id = (select public.current_organization_id())
      and s.source_mode = 'PublicCloud'
      and p.user_id = (select auth.uid())));
create policy examtransfer_public_submission_owner_insert on storage.objects for insert to authenticated with check (
  bucket_id = 'public-submission-archives'
  and owner_id = (select auth.uid())::text
  and (storage.foldername(name))[1] = (select public.current_organization_id())::text
  and (storage.foldername(name))[2] = 'public-submissions'
  and (storage.foldername(name))[3] = (select auth.uid())::text
  and array_length(storage.foldername(name), 1) = 4
  and exists (
    select 1 from public.submission_files f
    join public.submissions s on s.id = f.submission_id
    join public.session_participants p on p.id = s.participant_id
    where s.id::text = (storage.foldername(name))[4]
      and f.cloud_object_path = name
      and f.archive_signature_verified = false
      and s.status = 'Uploading'
      and s.source_mode = 'PublicCloud'
      and p.user_id = (select auth.uid())));

drop policy if exists examtransfer_device_receive on realtime.messages;
drop policy if exists examtransfer_device_send on realtime.messages;
drop policy if exists examtransfer_broadcast_receive on realtime.messages;
drop policy if exists examtransfer_broadcast_send on realtime.messages;
drop policy if exists examtransfer_presence_receive on realtime.messages;
drop policy if exists examtransfer_presence_send on realtime.messages;

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
          or exists (select 1 from public.public_device_connections c where c.session_id = s.id
            and (select realtime.topic()) = 'exam-session:' || s.id::text || ':device:' || c.device_id)))));

create policy examtransfer_broadcast_send on realtime.messages
for insert to authenticated with check (
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
          or exists (select 1 from public.public_device_connections c where c.session_id = s.id
            and (select realtime.topic()) = 'exam-session:' || s.id::text || ':device:' || c.device_id)))));

create policy examtransfer_presence_receive on realtime.messages
for select to authenticated using (
  extension = 'presence' and (
    exists (
      select 1 from public.public_device_connections c
      where c.user_id = (select auth.uid())
        and (select realtime.topic()) = 'exam-session:' || c.session_id::text)
    or exists (
      select 1 from public.profiles p
      join public.exam_sessions s on s.organization_id = p.organization_id
      join public.exams e on e.id = s.exam_id and e.organization_id = s.organization_id
      where p.id = (select auth.uid()) and p.is_active = true
        and p.role in ('Admin','Teacher') and s.access_mode = 'PublicCloud'
        and (p.role = 'Admin' or e.created_by = p.id)
        and (select realtime.topic()) = 'exam-session:' || s.id::text)));

create policy examtransfer_presence_send on realtime.messages
for insert to authenticated with check (
  extension = 'presence' and (
    exists (
      select 1 from public.public_device_connections c
      where c.user_id = (select auth.uid())
        and (select realtime.topic()) = 'exam-session:' || c.session_id::text)
    or exists (
      select 1 from public.profiles p
      join public.exam_sessions s on s.organization_id = p.organization_id
      join public.exams e on e.id = s.exam_id and e.organization_id = s.organization_id
      where p.id = (select auth.uid()) and p.is_active = true
        and p.role in ('Admin','Teacher') and s.access_mode = 'PublicCloud'
        and (p.role = 'Admin' or e.created_by = p.id)
        and (select realtime.topic()) = 'exam-session:' || s.id::text)));

grant select on public.class_enrollment_requests to authenticated;
grant update (status, decided_at, decided_by, updated_at) on public.class_enrollment_requests to authenticated;
grant select, insert, update, delete on public.public_class_assignments to authenticated;
revoke insert, update, delete on public.public_device_connections from authenticated;
revoke insert, update, delete on public.public_device_commands from authenticated;
revoke insert, update, delete on public.public_device_command_results from authenticated;
grant select on public.public_device_connections, public.public_device_commands,
  public.public_device_command_results to authenticated;

insert into public.examtransfer_cloud_meta(id, schema_version, updated_at)
values (1, 13, now())
on conflict (id) do update set schema_version = excluded.schema_version, updated_at = excluded.updated_at;

commit;
