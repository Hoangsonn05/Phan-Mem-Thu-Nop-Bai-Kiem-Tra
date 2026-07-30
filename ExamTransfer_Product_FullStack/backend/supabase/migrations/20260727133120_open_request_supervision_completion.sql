begin;

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
  v_connection_id uuid;
  v_participant_id uuid;
  v_connection_device_id text;
  v_class_id uuid;
  v_id uuid := gen_random_uuid();
begin
  if length(btrim(coalesce(p_violation_type, ''))) not between 1 and 128 then
    raise exception 'VIOLATION_TYPE_INVALID' using errcode = '22023';
  end if;
  if pg_column_size(coalesce(p_evidence_metadata, '{}'::jsonb)) > 65536 then
    raise exception 'VIOLATION_EVIDENCE_TOO_LARGE' using errcode = '22023';
  end if;

  select c.id, c.participant_id, c.device_id, s.class_id
  into v_connection_id, v_participant_id, v_connection_device_id, v_class_id
  from public.public_device_connections c
  join public.exam_sessions s
    on s.id = c.session_id
   and s.organization_id = c.organization_id
  join public.session_participants p
    on p.id = c.participant_id
   and p.session_id = c.session_id
   and p.organization_id = c.organization_id
   and p.user_id = c.user_id
  where c.session_id = p_session_id
    and c.device_id = btrim(p_device_id)
    and c.user_id = v_profile.id
    and c.organization_id = v_profile.organization_id
    and c.source_mode = 'PublicCloud'
    and p.source_mode = 'PublicCloud'
    and s.access_mode = 'PublicCloud'
    and s.status in ('Waiting','Distributing','InProgress','Paused','Collecting')
    and (
      (
        s.admission_mode = 'OpenRequest'
        and p.status = 'Approved'
      )
      or (
        s.admission_mode = 'ClassMembersOnly'
        and s.class_id is not null
        and exists (
          select 1
          from public.class_members m
          where m.class_id = s.class_id
            and m.user_id = c.user_id
            and m.organization_id = c.organization_id
        )
      )
    );
  if not found then
    raise exception 'DEVICE_CONNECTION_NOT_FOUND' using errcode = 'P0002';
  end if;

  insert into public.violations(
    id, organization_id, class_id, session_id, participant_id, device_id,
    type, severity, occurred_at, payload_json, evidence_metadata, status,
    source_mode, cloud_version, created_at, updated_at)
  values (
    v_id, v_profile.organization_id, v_class_id, p_session_id,
    v_participant_id, v_connection_device_id,
    btrim(p_violation_type),
    case when lower(p_violation_type) in ('tamper','agentstopped','processterminated') then 'High' else 'Warning' end,
    now(), '{}'::jsonb, coalesce(p_evidence_metadata, '{}'::jsonb), 'Open',
    'PublicCloud', private.next_public_cloud_version(), now(), now());

  update public.public_device_connections
  set violation_count = violation_count + 1,
      cloud_version = private.next_public_cloud_version(),
      updated_at = now()
  where id = v_connection_id;

  return v_id;
end
$function$;

revoke all on function public.report_public_violation(uuid,text,text,jsonb)
  from public, anon;
grant execute on function public.report_public_violation(uuid,text,text,jsonb)
  to authenticated;

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
    on c.session_id = d.session_id
   and c.device_id = d.device_id
   and c.organization_id = d.organization_id
  join public.exam_sessions s
    on s.id = d.session_id
   and s.organization_id = d.organization_id
  join public.session_participants p
    on p.id = c.participant_id
   and p.session_id = c.session_id
   and p.organization_id = c.organization_id
   and p.user_id = c.user_id
  where d.command_id = p_command_id
    and d.device_id = btrim(p_device_id)
    and d.organization_id = v_profile.organization_id
    and d.source_mode = 'PublicCloud'
    and c.user_id = v_profile.id
    and c.organization_id = v_profile.organization_id
    and c.source_mode = 'PublicCloud'
    and p.source_mode = 'PublicCloud'
    and s.access_mode = 'PublicCloud'
    and (
      (
        s.admission_mode = 'OpenRequest'
        and p.status = 'Approved'
      )
      or (
        s.admission_mode = 'ClassMembersOnly'
        and s.class_id is not null
        and exists (
          select 1
          from public.class_members m
          where m.class_id = s.class_id
            and m.user_id = c.user_id
            and m.organization_id = c.organization_id
        )
      )
    );
  if not found then
    raise exception 'DEVICE_COMMAND_NOT_FOUND' using errcode = 'P0002';
  end if;
  if v_command.expires_at <= v_now and p_status not in ('Expired','Ignored') then
    raise exception 'DEVICE_COMMAND_EXPIRED' using errcode = '55000';
  end if;

  select * into v_existing
  from public.public_device_command_results
  where command_id = p_command_id
  for update;
  if found then
    if v_existing.status in ('Executed','Failed','Expired','Ignored') then
      if v_existing.status = p_status then
        return v_existing.status;
      end if;
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

revoke all on function public.ack_public_device_command(uuid,text,text,text,text)
  from public, anon;
grant execute on function public.ack_public_device_command(uuid,text,text,text,text)
  to authenticated;

update public.examtransfer_cloud_meta
set schema_version = 20,
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
      'report_public_violation',
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
