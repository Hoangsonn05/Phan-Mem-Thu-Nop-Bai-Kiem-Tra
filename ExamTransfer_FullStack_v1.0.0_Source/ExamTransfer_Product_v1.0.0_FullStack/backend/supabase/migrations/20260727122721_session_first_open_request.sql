begin;

alter table public.exam_sessions
  add column if not exists admission_mode text not null default 'ClassMembersOnly';

update public.exam_sessions
set admission_mode = case
  when class_id is null then 'OpenRequest'
  else 'ClassMembersOnly'
end;

do $block$
begin
  if not exists (
    select 1
    from pg_catalog.pg_constraint
    where conname = 'exam_sessions_admission_mode_check'
      and conrelid = 'public.exam_sessions'::regclass
  ) then
    alter table public.exam_sessions
      add constraint exam_sessions_admission_mode_check
      check (admission_mode in ('ClassMembersOnly','OpenRequest'));
  end if;
end
$block$;

create index if not exists ix_exam_sessions_open_public_room
  on public.exam_sessions(organization_id, room_code)
  where access_mode = 'PublicCloud'
    and admission_mode = 'OpenRequest'
    and status = 'Waiting'
    and accepting_participants = true;

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
  v_participant_id uuid;
  v_status text;
begin
  if length(btrim(coalesce(p_device_id, ''))) not between 1 and 128 then
    raise exception 'DEVICE_ID_INVALID' using errcode = '22023';
  end if;
  if pg_column_size(coalesce(p_capability_json, '{}'::jsonb)) > 32768 then
    raise exception 'CAPABILITY_PAYLOAD_TOO_LARGE' using errcode = '22023';
  end if;
  perform pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(p_session_id::text, 0));

  select s.* into v_session
  from public.exam_sessions s
  join public.exams e
    on e.id = s.exam_id and e.organization_id = s.organization_id
  join public.classes c
    on c.id = s.class_id and c.organization_id = s.organization_id
  where s.id = p_session_id
    and s.organization_id = v_profile.organization_id
    and s.access_mode = 'PublicCloud'
    and s.admission_mode = 'ClassMembersOnly'
    and c.access_mode = 'Public';
  if not found then
    raise exception 'PUBLIC_CLASS_SESSION_NOT_FOUND' using errcode = 'P0002';
  end if;

  perform 1
  from public.class_members m
  where m.class_id = v_session.class_id
    and m.organization_id = v_profile.organization_id
    and m.user_id = v_profile.id
    and lower(btrim(m.student_code)) = lower(btrim(v_profile.student_code));
  if not found then
    raise exception 'CLASS_MEMBERSHIP_REQUIRED' using errcode = '42501';
  end if;

  if not exists (
    select 1 from public.public_class_assignments a
    where a.organization_id = v_profile.organization_id
      and a.class_id = v_session.class_id
      and a.exam_id = v_session.exam_id
      and (a.available_from is null or a.available_from <= pg_catalog.now())
      and (a.available_until is null or a.available_until >= pg_catalog.now())
  ) then
    raise exception 'PUBLIC_ASSIGNMENT_UNAVAILABLE' using errcode = '42501';
  end if;

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
    pg_catalog.now(), case when v_status = 'Approved' then pg_catalog.now() else null end,
    pg_catalog.now(), 'NotStarted', 'NotStarted', 0, false,
    coalesce(p_capability_json, '{}'::jsonb), 'PublicCloud',
    private.next_public_cloud_version(), pg_catalog.now(), pg_catalog.now());
  return v_participant_id;
end
$function$;

revoke all on function public.join_public_session(uuid,text,text,text,jsonb)
  from public, anon;
grant execute on function public.join_public_session(uuid,text,text,text,jsonb)
  to authenticated;

create or replace function public.join_open_public_session_by_room_code(
  p_room_code text,
  p_device_id text,
  p_machine_name text default null,
  p_app_version text default null,
  p_capability_json jsonb default '{}'::jsonb)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
  v_session public.exam_sessions%rowtype;
  v_exam public.exams%rowtype;
  v_participant public.session_participants%rowtype;
  v_room_code text := upper(btrim(coalesce(p_room_code, '')));
  v_status text;
  v_count integer;
begin
  if length(v_room_code) not between 4 and 12 then
    raise exception 'ROOM_CODE_INVALID' using errcode = '22023';
  end if;
  if length(btrim(coalesce(p_device_id, ''))) not between 1 and 128 then
    raise exception 'DEVICE_ID_INVALID' using errcode = '22023';
  end if;
  if pg_column_size(coalesce(p_capability_json, '{}'::jsonb)) > 32768 then
    raise exception 'CAPABILITY_PAYLOAD_TOO_LARGE' using errcode = '22023';
  end if;

  select count(*) into v_count
  from public.exam_sessions s
  where s.organization_id = v_profile.organization_id
    and s.room_code = v_room_code
    and s.access_mode = 'PublicCloud'
    and s.admission_mode = 'OpenRequest'
    and s.status = 'Waiting'
    and s.accepting_participants = true;
  if v_count = 0 then
    raise exception 'OPEN_PUBLIC_SESSION_NOT_FOUND' using errcode = 'P0002';
  elsif v_count > 1 then
    raise exception 'OPEN_PUBLIC_ROOM_CODE_AMBIGUOUS' using errcode = 'P0003';
  end if;

  select s.* into v_session
  from public.exam_sessions s
  where s.organization_id = v_profile.organization_id
    and s.room_code = v_room_code
    and s.access_mode = 'PublicCloud'
    and s.admission_mode = 'OpenRequest'
    and s.status = 'Waiting'
    and s.accepting_participants = true;

  perform pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(v_session.id::text, 0));
  select s.* into v_session
  from public.exam_sessions s
  where s.id = v_session.id
    and s.organization_id = v_profile.organization_id
    and s.access_mode = 'PublicCloud'
    and s.admission_mode = 'OpenRequest'
    and s.status = 'Waiting'
    and s.accepting_participants = true
  for update;
  if not found then
    raise exception 'SESSION_NOT_ACCEPTING_PARTICIPANTS' using errcode = '55000';
  end if;

  select e.* into v_exam
  from public.exams e
  where e.id = v_session.exam_id
    and e.organization_id = v_session.organization_id;
  if not found then
    raise exception 'PUBLIC_EXAM_NOT_FOUND' using errcode = 'P0002';
  end if;

  select p.* into v_participant
  from public.session_participants p
  where p.session_id = v_session.id
    and (
      p.user_id = v_profile.id
      or lower(btrim(p.student_code)) = lower(btrim(v_profile.student_code))
    )
  order by case when p.user_id = v_profile.id then 0 else 1 end
  limit 1;
  if found then
    if v_participant.user_id is distinct from v_profile.id then
      raise exception 'PARTICIPANT_ACCOUNT_MISMATCH' using errcode = '42501';
    end if;
    if v_participant.device_id is distinct from btrim(p_device_id) then
      raise exception 'PARTICIPANT_DEVICE_CONFLICT' using errcode = '23505';
    end if;
  else
    if exists (
      select 1 from public.session_participants p
      where p.session_id = v_session.id
        and p.device_id = btrim(p_device_id)
    ) then
      raise exception 'PARTICIPANT_DEVICE_CONFLICT' using errcode = '23505';
    end if;
    if v_session.capacity is not null and (
      select count(*) from public.session_participants p
      where p.session_id = v_session.id and p.status <> 'Rejected'
    ) >= v_session.capacity then
      raise exception 'SESSION_CAPACITY_REACHED' using errcode = '54000';
    end if;

    v_status := case when v_session.auto_approve then 'Approved' else 'PendingApproval' end;
    insert into public.session_participants(
      id, organization_id, session_id, user_id, student_code, display_name,
      class_name, device_id, machine_name, app_version, status, joined_at,
      approved_at, last_seen_at, download_status, submission_status,
      extra_time_minutes, resubmit_allowed, capability_json, source_mode,
      cloud_version, created_at, updated_at)
    values (
      gen_random_uuid(), v_profile.organization_id, v_session.id,
      v_profile.id, btrim(v_profile.student_code), v_profile.display_name, null,
      btrim(p_device_id), nullif(btrim(p_machine_name), ''),
      nullif(btrim(p_app_version), ''), v_status, pg_catalog.now(),
      case when v_status = 'Approved' then pg_catalog.now() else null end,
      pg_catalog.now(), 'NotStarted', 'NotStarted', 0, false,
      coalesce(p_capability_json, '{}'::jsonb), 'PublicCloud',
      private.next_public_cloud_version(), pg_catalog.now(), pg_catalog.now())
    returning * into v_participant;
  end if;

  return jsonb_build_object(
    'sessionId', v_session.id,
    'examId', v_session.exam_id,
    'participantId', v_participant.id,
    'participantStatus', v_participant.status,
    'sessionStatus', v_session.status,
    'roomCode', v_session.room_code,
    'examTitle', v_exam.title,
    'subject', v_exam.subject,
    'durationMinutes', v_exam.duration_minutes,
    'deliveryType', v_session.delivery_type,
    'supervisionMode', v_session.supervision_mode,
    'quizResultPolicy', v_session.quiz_result_policy,
    'plannedStartUtc', v_session.planned_start_at,
    'capacity', v_session.capacity,
    'currentParticipantCount', (
      select count(*) from public.session_participants p
      where p.session_id = v_session.id and p.status <> 'Rejected'
    ));
end
$function$;

revoke all on function public.join_open_public_session_by_room_code(text,text,text,text,jsonb)
  from public, anon;
grant execute on function public.join_open_public_session_by_room_code(text,text,text,text,jsonb)
  to authenticated;

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
  perform pg_catalog.pg_advisory_xact_lock(
    pg_catalog.hashtextextended(p_session_id::text || ':' || v_profile.id::text, 0));

  select s.* into v_session
  from public.exam_sessions s
  join public.exams e
    on e.id = s.exam_id and e.organization_id = s.organization_id
  where s.id = p_session_id
    and s.organization_id = v_profile.organization_id
    and s.access_mode = 'PublicCloud'
    and e.delivery_type = 'FileSubmission';
  if not found then
    raise exception 'PUBLIC_FILE_SESSION_NOT_FOUND' using errcode = 'P0002';
  end if;
  if v_session.status not in ('InProgress','Collecting') then
    raise exception 'SUBMISSION_WINDOW_CLOSED' using errcode = '55000';
  end if;

  select p.* into v_participant
  from public.session_participants p
  where p.session_id = p_session_id
    and p.organization_id = v_profile.organization_id
    and p.user_id = v_profile.id
    and p.status = 'Approved'
    and p.source_mode = 'PublicCloud'
    and (
      v_session.admission_mode = 'OpenRequest'
      or (
        v_session.admission_mode = 'ClassMembersOnly'
        and exists (
          select 1 from public.class_members m
          where m.class_id = v_session.class_id
            and m.user_id = v_profile.id
            and m.organization_id = v_profile.organization_id
        )
      )
    );
  if not found then
    raise exception 'APPROVED_PARTICIPANT_REQUIRED' using errcode = '42501';
  end if;

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

  v_attempt := coalesce((
    select max(attempt_number)
    from public.submissions
    where participant_id = v_participant.id), 0) + 1;
  if v_session.started_at is null then
    raise exception 'SESSION_NOT_STARTED' using errcode = '55000';
  end if;
  v_deadline := v_session.started_at
    + make_interval(mins => (
        select e.duration_minutes
        from public.exams e
        where e.id = v_session.exam_id
          and e.organization_id = v_session.organization_id))
    + make_interval(mins => greatest(v_participant.extra_time_minutes, 0));
  if v_session.admission_mode = 'ClassMembersOnly' then
    select least(v_deadline, coalesce(a.available_until, v_deadline))
    into v_deadline
    from public.public_class_assignments a
    where a.organization_id = v_profile.organization_id
      and a.class_id = v_session.class_id
      and a.exam_id = v_session.exam_id
      and (a.available_from is null or a.available_from <= pg_catalog.now())
      and (a.available_until is null or a.available_until >= pg_catalog.now());
    if not found then
      raise exception 'PUBLIC_ASSIGNMENT_UNAVAILABLE' using errcode = '42501';
    end if;
  end if;

  v_submission_id := gen_random_uuid();
  insert into public.submissions(
    id, organization_id, session_id, participant_id, attempt_number, status,
    deadline_at, is_late, is_official, idempotency_key, source_mode,
    cloud_version, created_at, updated_at)
  values (
    v_submission_id, v_profile.organization_id, p_session_id, v_participant.id,
    v_attempt, 'Uploading', v_deadline, false, false, btrim(p_idempotency_key),
    'PublicCloud', private.next_public_cloud_version(), pg_catalog.now(), pg_catalog.now());

  insert into public.submission_files(
    id, organization_id, submission_id, name, stored_name, mime_type,
    size_bytes, sha256, transfer_status, sync_status, cloud_object_path,
    archive_signature_verified, source_mode, cloud_version, created_at, updated_at)
  values (
    gen_random_uuid(), v_profile.organization_id, v_submission_id, v_safe_name,
    v_safe_name, 'application/octet-stream', p_size_bytes, lower(btrim(p_sha256)),
    'Pending', 'Pending', v_profile.organization_id::text || '/public-submissions/' ||
      v_profile.id::text || '/' || v_submission_id::text || '/' || v_safe_name,
    false, 'PublicCloud', private.next_public_cloud_version(),
    pg_catalog.now(), pg_catalog.now());

  update public.session_participants
  set submission_status = 'Uploading',
      resubmit_allowed = false,
      resubmit_reason = null,
      cloud_version = private.next_public_cloud_version(),
      updated_at = pg_catalog.now()
  where id = v_participant.id;
  return v_submission_id;
end
$function$;

revoke all on function public.init_public_submission(uuid,text,text,bigint,text)
  from public, anon;
grant execute on function public.init_public_submission(uuid,text,text,bigint,text)
  to authenticated;

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
  v_received timestamptz := pg_catalog.now();
  v_receipt text;
begin
  perform pg_catalog.pg_advisory_xact_lock(
    pg_catalog.hashtextextended(p_submission_id::text, 0));
  select s.* into v_submission
  from public.submissions s
  join public.session_participants p on p.id = s.participant_id
  join public.exam_sessions es on es.id = s.session_id
  where s.id = p_submission_id
    and s.organization_id = v_profile.organization_id
    and s.source_mode = 'PublicCloud'
    and p.user_id = v_profile.id
    and p.session_id = s.session_id
    and p.status = 'Approved'
    and p.source_mode = 'PublicCloud'
    and es.access_mode = 'PublicCloud'
    and es.status in ('InProgress','Collecting')
    and (
      es.admission_mode = 'OpenRequest'
      or (
        es.admission_mode = 'ClassMembersOnly'
        and exists (
          select 1 from public.class_members m
          where m.class_id = es.class_id
            and m.user_id = v_profile.id
            and m.organization_id = es.organization_id
        )
      )
    );
  if not found then
    raise exception 'PUBLIC_SUBMISSION_NOT_FOUND' using errcode = 'P0002';
  end if;
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
  ) then
    raise exception 'ARCHIVE_OBJECT_NOT_FOUND' using errcode = 'P0002';
  end if;

  v_receipt := upper(substr(encode(
    extensions.digest(p_submission_id::text || ':' || v_received::text, 'sha256'),
    'hex'), 1, 16));
  update public.submissions
  set status = case when v_received > deadline_at then 'LateSubmitted' else 'Submitted' end,
      server_received_at = v_received,
      is_late = v_received > deadline_at,
      is_official = true,
      receipt_code = v_receipt,
      receipt_signature = encode(
        extensions.digest(id::text || ':' || v_receipt, 'sha256'), 'hex'),
      cloud_version = private.next_public_cloud_version(),
      updated_at = v_received
  where id = p_submission_id;
  update public.session_participants
  set submission_status = case
        when v_received > v_submission.deadline_at then 'LateSubmitted'
        else 'Submitted'
      end,
      cloud_version = private.next_public_cloud_version(),
      updated_at = v_received
  where id = v_submission.participant_id;
  return v_receipt;
end
$function$;

revoke all on function public.finalize_public_submission(uuid,text)
  from public, anon;
grant execute on function public.finalize_public_submission(uuid,text)
  to authenticated;

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
  where p.session_id = p_session_id
    and p.user_id = v_profile.id
    and p.organization_id = v_profile.organization_id
    and p.status in ('Approved','Disconnected')
    and p.source_mode = 'PublicCloud'
    and s.access_mode = 'PublicCloud'
    and s.status in ('Waiting','Distributing','InProgress','Paused','Collecting')
    and (
      s.admission_mode = 'OpenRequest'
      or (
        s.admission_mode = 'ClassMembersOnly'
        and exists (
          select 1 from public.class_members m
          where m.class_id = s.class_id
            and m.user_id = v_profile.id
            and m.organization_id = s.organization_id
        )
      )
    );
  if not found then
    raise exception 'PUBLIC_PARTICIPANT_NOT_ACTIVE' using errcode = '42501';
  end if;

  insert into public.public_device_connections(
    organization_id, session_id, participant_id, user_id, device_id,
    connection_state, heartbeat_at, foreground_application,
    running_process_summary, app_version, agent_version, source_mode,
    cloud_version, created_at, updated_at)
  values (
    v_profile.organization_id, p_session_id, v_participant.id, v_profile.id,
    btrim(p_device_id), p_connection_state, pg_catalog.now(),
    left(nullif(btrim(p_foreground_application), ''), 512),
    coalesce(p_running_process_summary, '[]'::jsonb),
    left(nullif(btrim(p_app_version), ''), 64),
    left(nullif(btrim(p_agent_version), ''), 64),
    'PublicCloud', private.next_public_cloud_version(),
    pg_catalog.now(), pg_catalog.now())
  on conflict (session_id, device_id) do update
    set connection_state = excluded.connection_state,
        heartbeat_at = pg_catalog.now(),
        foreground_application = excluded.foreground_application,
        running_process_summary = excluded.running_process_summary,
        app_version = excluded.app_version,
        agent_version = excluded.agent_version,
        cloud_version = private.next_public_cloud_version(),
        updated_at = pg_catalog.now()
    where public.public_device_connections.user_id = v_profile.id
      and public.public_device_connections.participant_id = v_participant.id
  returning id into v_connection_id;
  if v_connection_id is null then
    raise exception 'DEVICE_OWNERSHIP_MISMATCH' using errcode = '42501';
  end if;

  update public.session_participants
  set last_seen_at = pg_catalog.now(),
      device_id = btrim(p_device_id),
      app_version = left(nullif(btrim(p_app_version), ''), 64),
      cloud_version = private.next_public_cloud_version(),
      updated_at = pg_catalog.now()
  where id = v_participant.id;
  return v_connection_id;
end
$function$;

revoke all on function public.upsert_public_device_heartbeat(uuid,text,text,text,jsonb,text,text)
  from public, anon;
grant execute on function public.upsert_public_device_heartbeat(uuid,text,text,text,jsonb,text,text)
  to authenticated;

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
  v_attempt_id uuid;
  v_deadline timestamptz;
  v_snapshot jsonb;
  v_max_score numeric(10,2);
begin
  if length(btrim(coalesce(p_idempotency_key, ''))) not between 8 and 128 then
    raise exception 'IDEMPOTENCY_KEY_INVALID' using errcode = '22023';
  end if;
  perform pg_catalog.pg_advisory_xact_lock(
    pg_catalog.hashtextextended(p_session_id::text || ':quiz:' || v_profile.id::text, 0));

  select s.* into v_session
  from public.exam_sessions s
  where s.id = p_session_id
    and s.organization_id = v_profile.organization_id
    and s.access_mode = 'PublicCloud'
    and s.status in ('InProgress','Paused')
    and s.delivery_type = 'MultipleChoice'
    and s.supervision_mode = 'Standard';
  if not found then
    raise exception 'PUBLIC_QUIZ_SESSION_NOT_ACTIVE' using errcode = 'P0002';
  end if;

  if v_session.admission_mode = 'ClassMembersOnly' then
    if not exists (
      select 1 from public.class_members m
      where m.class_id = v_session.class_id
        and m.organization_id = v_profile.organization_id
        and m.user_id = v_profile.id
    ) then
      raise exception 'CLASS_MEMBERSHIP_REQUIRED' using errcode = '42501';
    end if;
    if not exists (
      select 1 from public.public_class_assignments a
      where a.organization_id = v_profile.organization_id
        and a.class_id = v_session.class_id
        and a.exam_id = v_session.exam_id
        and (a.available_from is null or a.available_from <= pg_catalog.now())
        and (a.available_until is null or a.available_until >= pg_catalog.now())
    ) then
      raise exception 'PUBLIC_ASSIGNMENT_UNAVAILABLE' using errcode = '42501';
    end if;
  end if;

  select * into v_participant
  from public.session_participants
  where session_id = p_session_id
    and user_id = v_profile.id
    and organization_id = v_profile.organization_id
    and status = 'Approved'
    and source_mode = 'PublicCloud';
  if not found then
    raise exception 'APPROVED_PARTICIPANT_REQUIRED' using errcode = '42501';
  end if;
  if not exists (
    select 1 from public.public_device_connections d
    where d.session_id = p_session_id
      and d.participant_id = v_participant.id
      and d.organization_id = v_profile.organization_id
      and d.user_id = v_profile.id
      and d.connection_state in ('Online','Degraded')
      and d.policy_state = 'Applied'
      and (d.policy_lease_expires_at is null or d.policy_lease_expires_at > pg_catalog.now())
  ) then
    raise exception 'SUPERVISION_NOT_READY' using errcode = '42501';
  end if;

  select id into v_attempt_id
  from public.quiz_attempts
  where session_id = p_session_id and participant_id = v_participant.id;
  if found then return v_attempt_id; end if;
  if v_session.started_at is null then
    raise exception 'SESSION_NOT_STARTED' using errcode = '55000';
  end if;
  v_deadline := v_session.started_at
    + make_interval(mins => (
      select duration_minutes from public.exams
      where id = v_session.exam_id and organization_id = v_session.organization_id)
      + greatest(v_participant.extra_time_minutes, 0));
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
  where q.exam_id = v_session.exam_id
    and q.version = v_session.exam_version
    and q.organization_id = v_profile.organization_id;
  if v_max_score <= 0 then
    raise exception 'QUIZ_HAS_NO_QUESTIONS' using errcode = '55000';
  end if;

  v_attempt_id := gen_random_uuid();
  insert into public.quiz_attempts(
    id, organization_id, session_id, participant_id, exam_version, result_policy,
    status, started_at, deadline_at, max_score, snapshot_json,
    finalize_idempotency_key, source_mode, cloud_version, created_at, updated_at)
  values (
    v_attempt_id, v_profile.organization_id, p_session_id, v_participant.id,
    v_session.exam_version, v_session.quiz_result_policy, 'InProgress',
    pg_catalog.now(), v_deadline, v_max_score, v_snapshot, null, 'PublicCloud',
    private.next_public_cloud_version(), pg_catalog.now(), pg_catalog.now());
  return v_attempt_id;
end
$function$;

revoke all on function public.start_public_quiz_attempt(uuid,text) from public, anon;
grant execute on function public.start_public_quiz_attempt(uuid,text) to authenticated;

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
  where a.id = p_attempt_id
    and a.organization_id = v_profile.organization_id
    and a.source_mode = 'PublicCloud'
    and p.user_id = v_profile.id
    and p.organization_id = a.organization_id
    and s.access_mode = 'PublicCloud'
    and s.status in ('InProgress','Paused')
    and (
      s.admission_mode = 'OpenRequest'
      or (
        s.admission_mode = 'ClassMembersOnly'
        and exists (
          select 1 from public.class_members m
          where m.class_id = s.class_id
            and m.user_id = p.user_id
            and m.organization_id = s.organization_id
        )
      )
    );
  if not found then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_NOT_FOUND' using errcode = 'P0002';
  end if;
  if v_attempt.status <> 'InProgress' or pg_catalog.now() > v_attempt.deadline_at then
    raise exception 'QUIZ_ATTEMPT_CLOSED' using errcode = '55000';
  end if;
  select q.* into v_question
  from public.quiz_questions q
  join public.exam_sessions s on s.id = v_attempt.session_id
  where q.id = p_question_id
    and q.exam_id = s.exam_id
    and q.version = v_attempt.exam_version
    and q.organization_id = v_profile.organization_id;
  if not found then
    raise exception 'QUIZ_QUESTION_NOT_FOUND' using errcode = 'P0002';
  end if;
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
        where c.id = value::uuid
          and c.question_id = p_question_id
          and c.organization_id = v_profile.organization_id)
      else false
    end
  ) then
    raise exception 'QUIZ_CHOICE_INVALID' using errcode = '22023';
  end if;

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
    p_choice_ids, p_revision,
    least(coalesce(p_client_updated_at, pg_catalog.now()), pg_catalog.now()),
    'PublicCloud', private.next_public_cloud_version(),
    pg_catalog.now(), pg_catalog.now())
  on conflict (attempt_id, question_id) do update
    set choice_ids = excluded.choice_ids,
        revision = excluded.revision,
        client_updated_at = excluded.client_updated_at,
        cloud_version = private.next_public_cloud_version(),
        updated_at = pg_catalog.now()
    where excluded.revision > public.quiz_answers.revision;
  return greatest(p_revision, coalesce(v_existing_revision, 0));
end
$function$;

revoke all on function public.save_public_quiz_answers(uuid,uuid,jsonb,bigint,timestamptz)
  from public, anon;
grant execute on function public.save_public_quiz_answers(uuid,uuid,jsonb,bigint,timestamptz)
  to authenticated;

create or replace function public.get_public_student_timeline(p_session_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
  v_result jsonb;
begin
  select jsonb_build_object(
    'sessionId', s.id,
    'participantId', p.id,
    'participantStatus', p.status,
    'submissionStatus', p.submission_status,
    'sessionStatus', s.status,
    'admissionMode', s.admission_mode,
    'examId', s.exam_id,
    'examTitle', e.title,
    'subject', e.subject,
    'examVersion', s.exam_version,
    'deliveryType', s.delivery_type,
    'supervisionMode', s.supervision_mode,
    'resultPolicy', s.quiz_result_policy,
    'startedAtUtc', s.started_at,
    'durationMinutes', e.duration_minutes,
    'extraTimeMinutes', p.extra_time_minutes,
    'effectiveDeadlineUtc', case when s.started_at is null then null
      else s.started_at + make_interval(
        mins => e.duration_minutes + greatest(p.extra_time_minutes, 0)) end,
    'attemptId', a.id,
    'attemptStatus', a.status,
    'attemptDeadlineUtc', a.deadline_at,
    'scoreVisible', a.status = 'Finalized' and a.result_policy = 'ShowAfterSubmission',
    'score', case
      when a.status = 'Finalized' and a.result_policy = 'ShowAfterSubmission'
      then a.score else null end,
    'maxScore', case
      when a.status = 'Finalized' and a.result_policy = 'ShowAfterSubmission'
      then a.max_score else null end,
    'serverNowUtc', pg_catalog.clock_timestamp(),
    'revision', greatest(p.cloud_version, coalesce(a.cloud_version, p.cloud_version)),
    'updatedAtUtc', greatest(p.updated_at, coalesce(a.updated_at, p.updated_at)))
  into v_result
  from public.exam_sessions s
  join public.exams e
    on e.id = s.exam_id and e.organization_id = s.organization_id
  join public.session_participants p
    on p.session_id = s.id and p.organization_id = s.organization_id
   and p.user_id = v_profile.id and p.source_mode = 'PublicCloud'
  left join public.quiz_attempts a
    on a.session_id = s.id and a.participant_id = p.id
   and a.organization_id = s.organization_id and a.source_mode = 'PublicCloud'
  where s.id = p_session_id
    and s.organization_id = v_profile.organization_id
    and s.access_mode = 'PublicCloud'
    and (
      s.admission_mode = 'OpenRequest'
      or (
        s.admission_mode = 'ClassMembersOnly'
        and exists (
          select 1 from public.class_members m
          where m.class_id = s.class_id
            and m.organization_id = s.organization_id
            and m.user_id = v_profile.id
        )
      )
    );
  if v_result is null then
    raise exception 'PUBLIC_STUDENT_TIMELINE_NOT_FOUND' using errcode = 'P0002';
  end if;
  return v_result;
end
$function$;

revoke all on function public.get_public_student_timeline(uuid) from public, anon;
grant execute on function public.get_public_student_timeline(uuid) to authenticated;

create or replace function public.get_public_exam_manifest(p_session_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
  v_session public.exam_sessions%rowtype;
begin
  select s.* into v_session
  from public.exam_sessions s
  join public.session_participants p
    on p.session_id = s.id
   and p.organization_id = s.organization_id
   and p.user_id = v_profile.id
   and p.status = 'Approved'
   and p.source_mode = 'PublicCloud'
  where s.id = p_session_id
    and s.organization_id = v_profile.organization_id
    and s.access_mode = 'PublicCloud'
    and (
      (
        s.admission_mode = 'OpenRequest'
        and s.status in ('InProgress','Collecting')
      )
      or (
        s.admission_mode = 'ClassMembersOnly'
        and s.status in ('Waiting','InProgress','Collecting')
        and exists (
          select 1 from public.class_members m
          where m.class_id = s.class_id
            and m.user_id = v_profile.id
            and m.organization_id = s.organization_id
        )
        and exists (
          select 1 from public.public_class_assignments a
          where a.class_id = s.class_id
            and a.exam_id = s.exam_id
            and a.organization_id = s.organization_id
            and (a.available_from is null or a.available_from <= pg_catalog.now())
            and (a.available_until is null or a.available_until >= pg_catalog.now())
        )
      )
    );
  if not found then
    raise exception 'PUBLIC_EXAM_MANIFEST_FORBIDDEN' using errcode = '42501';
  end if;

  return coalesce((
    select jsonb_agg(jsonb_build_object(
      'id', f.id,
      'name', f.name,
      'size_bytes', f.size_bytes,
      'sha256', lower(f.sha256),
      'mime_type', f.mime_type)
      order by f.name, f.id)
    from public.exam_files f
    where f.exam_id = v_session.exam_id
      and f.organization_id = v_session.organization_id
      and f.cloud_object_path is not null
  ), '[]'::jsonb);
end
$function$;

revoke all on function public.get_public_exam_manifest(uuid) from public, anon;
grant execute on function public.get_public_exam_manifest(uuid) to authenticated;

create or replace function public.get_public_exam_file_download(
  p_session_id uuid,
  p_file_id uuid)
returns table(object_path text, file_name text, size_bytes bigint, sha256 text)
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
begin
  return query
  select f.cloud_object_path, f.name, f.size_bytes, lower(f.sha256)
  from public.exam_files f
  join public.exams e
    on e.id = f.exam_id and e.organization_id = f.organization_id
  join public.exam_sessions s
    on s.exam_id = e.id and s.organization_id = e.organization_id
  join public.session_participants p
    on p.session_id = s.id
   and p.organization_id = s.organization_id
   and p.user_id = v_profile.id
   and p.status = 'Approved'
   and p.source_mode = 'PublicCloud'
  where s.id = p_session_id
    and f.id = p_file_id
    and s.organization_id = v_profile.organization_id
    and s.access_mode = 'PublicCloud'
    and (
      (
        s.admission_mode = 'OpenRequest'
        and s.status in ('InProgress','Collecting')
      )
      or (
        s.admission_mode = 'ClassMembersOnly'
        and s.status in ('Waiting','InProgress','Collecting')
        and exists (
          select 1 from public.class_members m
          where m.class_id = s.class_id
            and m.user_id = v_profile.id
            and m.organization_id = s.organization_id
        )
        and exists (
          select 1 from public.public_class_assignments a
          where a.class_id = s.class_id
            and a.exam_id = s.exam_id
            and a.organization_id = s.organization_id
            and (a.available_from is null or a.available_from <= pg_catalog.now())
            and (a.available_until is null or a.available_until >= pg_catalog.now())
        )
      )
    )
    and f.cloud_object_path is not null;
  if not found then
    raise exception 'PUBLIC_EXAM_FILE_FORBIDDEN' using errcode = '42501';
  end if;
end
$function$;

revoke all on function public.get_public_exam_file_download(uuid,uuid) from public, anon;
grant execute on function public.get_public_exam_file_download(uuid,uuid) to authenticated;

update public.examtransfer_cloud_meta
set schema_version = 19,
    updated_at = pg_catalog.now()
where id = 1;

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
    'schemaVersion',
      (select schema_version from public.examtransfer_cloud_meta where id = 1),
    'criticalRpcs', jsonb_build_array(
      'join_public_session','join_open_public_session_by_room_code',
      'init_public_submission','finalize_public_submission',
      'upsert_public_device_heartbeat','ack_public_device_command',
      'start_public_quiz_attempt','save_public_quiz_answers',
      'finalize_public_quiz_attempt','get_public_quiz_attempt',
      'get_teacher_quiz_attempts','verify_public_submission_archive',
      'get_public_exam_manifest','get_public_exam_file_download',
      'approve_public_participant','reject_public_participant',
      'bulk_approve_public_participants','add_public_participant_extra_time',
      'allow_public_resubmission','reject_public_submission',
      'approve_public_enrollment_request','reject_public_enrollment_request',
      'get_public_student_timeline'),
    'buckets', coalesce((
      select jsonb_agg(id order by id)
      from storage.buckets
      where id in ('exam-archives','public-submission-archives')
    ), '[]'::jsonb));
end
$function$;

revoke all on function public.get_examtransfer_cloud_capabilities()
  from public, anon;
grant execute on function public.get_examtransfer_cloud_capabilities()
  to authenticated, service_role;

commit;
