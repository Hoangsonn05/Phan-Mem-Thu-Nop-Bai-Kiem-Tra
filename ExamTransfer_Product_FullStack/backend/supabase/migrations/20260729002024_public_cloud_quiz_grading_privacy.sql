begin;

-- Schema 22 exposed grade values on a session-wide topic. Keep the existing
-- trigger name for forward compatibility, but emit only an invalidation signal
-- to every device owned by the attempt participant.
create or replace function private.notify_public_quiz_grade_returned()
returns trigger
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_device record;
  v_event_type text;
begin
  if new.source_mode <> 'PublicCloud'
     or old.returned_at is not distinct from new.returned_at then
    return new;
  end if;

  v_event_type := case
    when new.returned_at is null then 'QuizGradeReopened'
    else 'QuizGradeReturned'
  end;

  for v_device in
    select distinct d.device_id
    from public.public_device_connections d
    join public.session_participants p
      on p.id = d.participant_id
     and p.session_id = d.session_id
     and p.organization_id = d.organization_id
     and p.user_id = d.user_id
    where d.session_id = new.session_id
      and d.participant_id = new.participant_id
      and d.organization_id = new.organization_id
      and d.source_mode = 'PublicCloud'
      and p.source_mode = 'PublicCloud'
      and length(btrim(d.device_id)) > 0
  loop
    perform realtime.send(
      jsonb_build_object(
        'eventType', v_event_type,
        'attemptId', new.id,
        'sessionId', new.session_id),
      v_event_type,
      'exam-session:' || new.session_id::text || ':device:' || v_device.device_id,
      true);
  end loop;

  return new;
end
$function$;
revoke all on function private.notify_public_quiz_grade_returned()
  from public, anon, authenticated, service_role;

drop trigger if exists quiz_attempts_notify_grade_returned
  on public.quiz_attempts;
create trigger quiz_attempts_notify_grade_returned
after update of returned_at on public.quiz_attempts
for each row execute function private.notify_public_quiz_grade_returned();

create or replace function private.public_quiz_grade_result(p_attempt_id uuid)
returns jsonb
language sql
stable
security definer
set search_path = ''
as $function$
  select jsonb_build_object(
    'attemptId', a.id,
    'sessionId', a.session_id,
    'participantId', a.participant_id,
    'autoScore', a.auto_score,
    'score', a.score,
    'maxScore', 10.00,
    'gradingStatus', a.grading_status,
    'generalComment', a.general_comment,
    'graderId', a.grader_id,
    'gradedAt', a.graded_at,
    'returnedAt', a.returned_at,
    'cloudVersion', a.cloud_version,
    'updatedAt', a.updated_at)
  from public.quiz_attempts a
  where a.id = p_attempt_id
$function$;
revoke all on function private.public_quiz_grade_result(uuid)
  from public, anon, authenticated, service_role;

create or replace function public.save_public_quiz_grade(
  p_attempt_id uuid,
  p_score numeric,
  p_general_comment text,
  p_expected_cloud_version bigint,
  p_request_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_attempt public.quiz_attempts%rowtype;
  v_session public.exam_sessions%rowtype;
  v_cached jsonb;
  v_result jsonb;
  v_score numeric(10,2);
begin
  select * into v_attempt
  from public.quiz_attempts
  where id = p_attempt_id and source_mode = 'PublicCloud';
  if not found then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_NOT_FOUND' using errcode = 'P0002';
  end if;

  v_session := private.require_public_session_teacher(v_attempt.session_id);
  if v_attempt.organization_id <> v_session.organization_id
     or not exists (
       select 1
       from public.session_participants p
       where p.id = v_attempt.participant_id
         and p.session_id = v_session.id
         and p.organization_id = v_session.organization_id
         and p.source_mode = 'PublicCloud'
     ) then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_ORGANIZATION_MISMATCH'
      using errcode = '42501';
  end if;
  if p_expected_cloud_version is null or p_expected_cloud_version < 1 then
    raise exception 'QUIZ_GRADE_VERSION_REQUIRED' using errcode = '22023';
  end if;

  v_score := coalesce(p_score, v_attempt.auto_score);
  if v_score is null or v_score < 0 or v_score > 10.00 then
    raise exception 'QUIZ_GRADE_SCORE_INVALID' using errcode = '22023';
  end if;

  v_cached := private.begin_public_teacher_mutation(
    p_request_id,
    v_session.organization_id,
    'SavePublicQuizGrade',
    jsonb_build_object(
      'attemptId', p_attempt_id,
      'expectedCloudVersion', p_expected_cloud_version,
      'score', v_score,
      'generalComment', nullif(btrim(coalesce(p_general_comment, '')), '')
    )::text);
  if v_cached is not null then
    return v_cached;
  end if;

  select * into v_attempt
  from public.quiz_attempts
  where id = p_attempt_id
    and organization_id = v_session.organization_id
    and source_mode = 'PublicCloud'
  for update;
  if v_attempt.cloud_version <> p_expected_cloud_version then
    raise exception 'QUIZ_GRADE_VERSION_CONFLICT' using errcode = '40001';
  end if;
  if v_attempt.status <> 'Finalized' then
    raise exception 'QUIZ_ATTEMPT_NOT_FINALIZED' using errcode = '55000';
  end if;
  if v_attempt.grading_status = 'Returned' then
    raise exception 'QUIZ_GRADE_REOPEN_REQUIRED' using errcode = '55000';
  end if;

  update public.quiz_attempts
  set score = v_score,
      max_score = 10.00,
      general_comment = nullif(btrim(coalesce(p_general_comment, '')), ''),
      grading_status = 'Graded',
      grader_id = (select auth.uid()),
      graded_at = now(),
      updated_at = now()
  where id = p_attempt_id
    and cloud_version = p_expected_cloud_version;
  if not found then
    raise exception 'QUIZ_GRADE_VERSION_CONFLICT' using errcode = '40001';
  end if;

  v_result := private.public_quiz_grade_result(p_attempt_id);
  perform private.write_public_teacher_audit(
    v_session.organization_id,
    v_session.id,
    'SavePublicQuizGrade',
    'quiz_attempts',
    p_attempt_id,
    p_request_id,
    to_jsonb(v_attempt),
    v_result);
  return private.finish_public_teacher_mutation(p_request_id, v_result);
end
$function$;

create or replace function public.return_public_quiz_grade(
  p_attempt_id uuid,
  p_message text,
  p_expected_cloud_version bigint,
  p_request_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_attempt public.quiz_attempts%rowtype;
  v_session public.exam_sessions%rowtype;
  v_cached jsonb;
  v_result jsonb;
begin
  select * into v_attempt
  from public.quiz_attempts
  where id = p_attempt_id and source_mode = 'PublicCloud';
  if not found then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_NOT_FOUND' using errcode = 'P0002';
  end if;

  v_session := private.require_public_session_teacher(v_attempt.session_id);
  if v_attempt.organization_id <> v_session.organization_id
     or not exists (
       select 1
       from public.session_participants p
       where p.id = v_attempt.participant_id
         and p.session_id = v_session.id
         and p.organization_id = v_session.organization_id
         and p.source_mode = 'PublicCloud'
     ) then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_ORGANIZATION_MISMATCH'
      using errcode = '42501';
  end if;
  if p_expected_cloud_version is null or p_expected_cloud_version < 1 then
    raise exception 'QUIZ_GRADE_VERSION_REQUIRED' using errcode = '22023';
  end if;

  v_cached := private.begin_public_teacher_mutation(
    p_request_id,
    v_session.organization_id,
    'ReturnPublicQuizGrade',
    jsonb_build_object(
      'attemptId', p_attempt_id,
      'expectedCloudVersion', p_expected_cloud_version,
      'message', nullif(btrim(coalesce(p_message, '')), '')
    )::text);
  if v_cached is not null then
    return v_cached;
  end if;

  select * into v_attempt
  from public.quiz_attempts
  where id = p_attempt_id
    and organization_id = v_session.organization_id
    and source_mode = 'PublicCloud'
  for update;
  if v_attempt.cloud_version <> p_expected_cloud_version then
    raise exception 'QUIZ_GRADE_VERSION_CONFLICT' using errcode = '40001';
  end if;
  if v_attempt.status <> 'Finalized'
     or v_attempt.grading_status <> 'Graded'
     or v_attempt.score is null
     or v_attempt.score < 0
     or v_attempt.score > 10.00 then
    raise exception 'QUIZ_GRADE_NOT_RETURNABLE' using errcode = '55000';
  end if;

  update public.quiz_attempts
  set grading_status = 'Returned',
      returned_at = now(),
      grader_id = (select auth.uid()),
      graded_at = coalesce(graded_at, now()),
      updated_at = now()
  where id = p_attempt_id
    and cloud_version = p_expected_cloud_version;
  if not found then
    raise exception 'QUIZ_GRADE_VERSION_CONFLICT' using errcode = '40001';
  end if;

  v_result := private.public_quiz_grade_result(p_attempt_id);
  perform private.write_public_teacher_audit(
    v_session.organization_id,
    v_session.id,
    'ReturnPublicQuizGrade',
    'quiz_attempts',
    p_attempt_id,
    p_request_id,
    to_jsonb(v_attempt),
    v_result || jsonb_build_object(
      'message', nullif(btrim(coalesce(p_message, '')), '')));
  return private.finish_public_teacher_mutation(p_request_id, v_result);
end
$function$;

create or replace function public.reopen_public_quiz_grade(
  p_attempt_id uuid,
  p_reason text,
  p_expected_cloud_version bigint,
  p_request_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_attempt public.quiz_attempts%rowtype;
  v_session public.exam_sessions%rowtype;
  v_cached jsonb;
  v_result jsonb;
begin
  if length(btrim(coalesce(p_reason, ''))) < 3 then
    raise exception 'QUIZ_GRADE_REOPEN_REASON_REQUIRED' using errcode = '22023';
  end if;

  select * into v_attempt
  from public.quiz_attempts
  where id = p_attempt_id and source_mode = 'PublicCloud';
  if not found then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_NOT_FOUND' using errcode = 'P0002';
  end if;

  v_session := private.require_public_session_teacher(v_attempt.session_id);
  if v_attempt.organization_id <> v_session.organization_id
     or not exists (
       select 1
       from public.session_participants p
       where p.id = v_attempt.participant_id
         and p.session_id = v_session.id
         and p.organization_id = v_session.organization_id
         and p.source_mode = 'PublicCloud'
     ) then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_ORGANIZATION_MISMATCH'
      using errcode = '42501';
  end if;
  if p_expected_cloud_version is null or p_expected_cloud_version < 1 then
    raise exception 'QUIZ_GRADE_VERSION_REQUIRED' using errcode = '22023';
  end if;

  v_cached := private.begin_public_teacher_mutation(
    p_request_id,
    v_session.organization_id,
    'ReopenPublicQuizGrade',
    jsonb_build_object(
      'attemptId', p_attempt_id,
      'expectedCloudVersion', p_expected_cloud_version,
      'reason', btrim(p_reason)
    )::text);
  if v_cached is not null then
    return v_cached;
  end if;

  select * into v_attempt
  from public.quiz_attempts
  where id = p_attempt_id
    and organization_id = v_session.organization_id
    and source_mode = 'PublicCloud'
  for update;
  if v_attempt.cloud_version <> p_expected_cloud_version then
    raise exception 'QUIZ_GRADE_VERSION_CONFLICT' using errcode = '40001';
  end if;
  if v_attempt.status <> 'Finalized'
     or v_attempt.grading_status <> 'Returned'
     or v_attempt.returned_at is null then
    raise exception 'QUIZ_GRADE_NOT_REOPENABLE' using errcode = '55000';
  end if;

  update public.quiz_attempts
  set grading_status = 'InProgress',
      returned_at = null,
      grader_id = (select auth.uid()),
      updated_at = now()
  where id = p_attempt_id
    and cloud_version = p_expected_cloud_version;
  if not found then
    raise exception 'QUIZ_GRADE_VERSION_CONFLICT' using errcode = '40001';
  end if;

  v_result := private.public_quiz_grade_result(p_attempt_id);
  perform private.write_public_teacher_audit(
    v_session.organization_id,
    v_session.id,
    'ReopenPublicQuizGrade',
    'quiz_attempts',
    p_attempt_id,
    p_request_id,
    to_jsonb(v_attempt),
    v_result || jsonb_build_object('reason', btrim(p_reason)));
  return private.finish_public_teacher_mutation(p_request_id, v_result);
end
$function$;

revoke all on function public.save_public_quiz_grade(uuid,numeric,text,bigint,uuid)
  from public, anon, authenticated, service_role;
revoke all on function public.return_public_quiz_grade(uuid,text,bigint,uuid)
  from public, anon, authenticated, service_role;
revoke all on function public.reopen_public_quiz_grade(uuid,text,bigint,uuid)
  from public, anon, authenticated, service_role;
grant execute on function public.save_public_quiz_grade(uuid,numeric,text,bigint,uuid)
  to authenticated;
grant execute on function public.return_public_quiz_grade(uuid,text,bigint,uuid)
  to authenticated;
grant execute on function public.reopen_public_quiz_grade(uuid,text,bigint,uuid)
  to authenticated;

update public.examtransfer_cloud_meta
set schema_version = 23,
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
      'get_public_quiz_attempt_review','get_teacher_quiz_attempts',
      'save_public_quiz_grade','return_public_quiz_grade',
      'reopen_public_quiz_grade',
      'verify_public_submission_archive',
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
