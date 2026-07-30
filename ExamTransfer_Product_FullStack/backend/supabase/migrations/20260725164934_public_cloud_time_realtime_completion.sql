begin;

-- ET-01 PublicCloud completion:
--   * keep the existing delta-minute and request-id contract;
--   * update an active quiz attempt in the same transaction;
--   * return database time and absolute deadlines;
--   * emit one private broadcast for the first logical mutation only.
create or replace function public.add_public_participant_extra_time(
  p_session_id uuid,
  p_participant_id uuid,
  p_minutes integer,
  p_reason text,
  p_request_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_session public.exam_sessions%rowtype := private.require_public_session_teacher(p_session_id);
  v_before public.session_participants%rowtype;
  v_participant public.session_participants%rowtype;
  v_attempt public.quiz_attempts%rowtype;
  v_cached jsonb;
  v_result jsonb;
  v_deadline timestamptz;
  v_server_now timestamptz;
  v_revision bigint;
begin
  if p_minutes < 1 or p_minutes > 480 or length(btrim(coalesce(p_reason,''))) < 3 then
    raise exception 'EXTRA_TIME_INPUT_INVALID' using errcode = '22023';
  end if;
  if v_session.status not in ('InProgress','Paused','Collecting') then
    raise exception 'SESSION_NOT_ACTIVE' using errcode = '55000';
  end if;

  v_cached := private.begin_public_teacher_mutation(
    p_request_id,
    v_session.organization_id,
    'AddPublicParticipantExtraTime',
    p_session_id::text || ':' || p_participant_id::text || ':' || p_minutes::text);
  if v_cached is not null then
    -- A retry must not mutate or broadcast again, but it still receives fresh DB time.
    return v_cached || jsonb_build_object('serverNowUtc', clock_timestamp());
  end if;

  select *
  into v_before
  from public.session_participants
  where id = p_participant_id
    and session_id = p_session_id
    and organization_id = v_session.organization_id
    and source_mode = 'PublicCloud'
  for update;
  if not found or v_before.status not in ('Approved','Disconnected') then
    raise exception 'PUBLIC_PARTICIPANT_NOT_ACTIVE' using errcode = 'P0002';
  end if;
  perform private.assert_public_participant_organization(v_before, v_session.organization_id);
  if v_before.extra_time_minutes + p_minutes > 480 then
    raise exception 'EXTRA_TIME_LIMIT_EXCEEDED' using errcode = '22023';
  end if;
  if v_session.started_at is null then
    raise exception 'SESSION_NOT_STARTED' using errcode = '55000';
  end if;

  update public.session_participants
  set extra_time_minutes = extra_time_minutes + p_minutes,
      updated_at = now()
  where id = p_participant_id
  returning * into v_participant;

  select v_session.started_at
    + make_interval(mins => e.duration_minutes + greatest(v_participant.extra_time_minutes, 0))
  into v_deadline
  from public.exams e
  where e.id = v_session.exam_id
    and e.organization_id = v_session.organization_id;
  if v_deadline is null then
    raise exception 'PUBLIC_EXAM_NOT_FOUND' using errcode = 'P0002';
  end if;

  update public.quiz_attempts
  set deadline_at = v_deadline,
      updated_at = now()
  where session_id = p_session_id
    and participant_id = p_participant_id
    and organization_id = v_session.organization_id
    and source_mode = 'PublicCloud'
    and status = 'InProgress'
  returning * into v_attempt;

  v_server_now := clock_timestamp();
  v_revision := greatest(
    v_participant.cloud_version,
    coalesce(v_attempt.cloud_version, v_participant.cloud_version));
  v_result := jsonb_build_object(
    'participantId', v_participant.id,
    'sessionId', v_participant.session_id,
    'status', v_participant.status,
    'approvedAt', v_participant.approved_at,
    'minutes', p_minutes,
    'extraTimeMinutes', v_participant.extra_time_minutes,
    'resubmitAllowed', v_participant.resubmit_allowed,
    'resubmitReason', v_participant.resubmit_reason,
    'cloudVersion', v_participant.cloud_version,
    'updatedAt', greatest(v_participant.updated_at, coalesce(v_attempt.updated_at, v_participant.updated_at)),
    'effectiveDeadline', v_deadline,
    'effectiveDeadlineUtc', v_deadline,
    'attemptId', v_attempt.id,
    'attemptStatus', v_attempt.status,
    'attemptDeadline', v_attempt.deadline_at,
    'attemptRevision', v_attempt.cloud_version,
    'serverNowUtc', v_server_now,
    'revision', v_revision,
    'requestId', p_request_id);

  perform private.write_public_teacher_audit(
    v_session.organization_id,
    p_session_id,
    'AddPublicParticipantExtraTime',
    'session_participants',
    p_participant_id,
    p_request_id,
    to_jsonb(v_before),
    v_result || jsonb_build_object('reason', btrim(p_reason)));

  -- The result is cached before the broadcast. A repeated request-id returns
  -- above and therefore cannot emit a duplicate logical TimeExtended event.
  v_result := private.finish_public_teacher_mutation(p_request_id, v_result);
  perform realtime.send(
    v_result,
    'TimeExtended',
    'exam-session:' || p_session_id::text,
    true);
  return v_result;
end
$function$;

revoke all on function public.add_public_participant_extra_time(uuid,uuid,integer,text,uuid)
  from public, anon;
grant execute on function public.add_public_participant_extra_time(uuid,uuid,integer,text,uuid)
  to authenticated;

-- One authoritative snapshot for PublicCloud reconnect and event-gap recovery.
-- It is intentionally scoped to the authenticated student's own participant.
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
    'sessionStatus', s.status,
    'startedAtUtc', s.started_at,
    'durationMinutes', e.duration_minutes,
    'extraTimeMinutes', p.extra_time_minutes,
    'effectiveDeadlineUtc', case
      when s.started_at is null then null
      else s.started_at + make_interval(mins => e.duration_minutes + greatest(p.extra_time_minutes, 0))
    end,
    'attemptId', a.id,
    'attemptStatus', a.status,
    'attemptDeadlineUtc', a.deadline_at,
    'serverNowUtc', clock_timestamp(),
    'revision', greatest(p.cloud_version, coalesce(a.cloud_version, p.cloud_version)),
    'updatedAtUtc', greatest(p.updated_at, coalesce(a.updated_at, p.updated_at)))
  into v_result
  from public.exam_sessions s
  join public.exams e
    on e.id = s.exam_id
   and e.organization_id = s.organization_id
  join public.session_participants p
    on p.session_id = s.id
   and p.organization_id = s.organization_id
   and p.user_id = v_profile.id
   and p.source_mode = 'PublicCloud'
  left join public.quiz_attempts a
    on a.session_id = s.id
   and a.participant_id = p.id
   and a.organization_id = s.organization_id
   and a.source_mode = 'PublicCloud'
  where s.id = p_session_id
    and s.organization_id = v_profile.organization_id
    and s.access_mode = 'PublicCloud'
    and exists (
      select 1
      from public.class_members m
      where m.class_id = s.class_id
        and m.organization_id = s.organization_id
        and m.user_id = v_profile.id);

  if v_result is null then
    raise exception 'PUBLIC_STUDENT_TIMELINE_NOT_FOUND' using errcode = 'P0002';
  end if;
  return v_result;
end
$function$;

revoke all on function public.get_public_student_timeline(uuid) from public, anon;
grant execute on function public.get_public_student_timeline(uuid) to authenticated;

update public.examtransfer_cloud_meta
set schema_version = 16,
    updated_at = now()
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
    'schemaVersion', (select schema_version from public.examtransfer_cloud_meta where id = 1),
    'criticalRpcs', jsonb_build_array(
      'join_public_session','init_public_submission','finalize_public_submission',
      'upsert_public_device_heartbeat','ack_public_device_command',
      'start_public_quiz_attempt','save_public_quiz_answers',
      'finalize_public_quiz_attempt','verify_public_submission_archive',
      'get_public_exam_file_download','approve_public_participant',
      'reject_public_participant','bulk_approve_public_participants',
      'add_public_participant_extra_time','allow_public_resubmission',
      'reject_public_submission','approve_public_enrollment_request',
      'reject_public_enrollment_request','get_public_student_timeline'),
    'buckets', coalesce((
      select jsonb_agg(id order by id)
      from storage.buckets
      where id in ('exam-archives','public-submission-archives')
    ), '[]'::jsonb)
  );
end
$function$;

revoke all on function public.get_examtransfer_cloud_capabilities() from public, anon;
grant execute on function public.get_examtransfer_cloud_capabilities() to authenticated, service_role;

commit;
