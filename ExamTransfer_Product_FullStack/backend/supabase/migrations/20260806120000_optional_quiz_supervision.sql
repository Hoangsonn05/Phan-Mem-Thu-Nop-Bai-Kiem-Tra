begin;

alter table public.exams drop constraint if exists exams_supervision_mode_check;
alter table public.exams add constraint exams_supervision_mode_check
  check (supervision_mode in ('None','Standard'));

alter table public.exam_sessions drop constraint if exists exam_sessions_workflow_snapshot_check;
alter table public.exam_sessions add constraint exam_sessions_workflow_snapshot_check
  check (delivery_type in ('FileSubmission','MultipleChoice')
    and supervision_mode in ('None','Standard')
    and quiz_result_policy in ('Hidden','ShowAfterSubmission')
    and exam_version > 0);

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
  v_attempt public.quiz_attempts%rowtype;
  v_attempt_id uuid;
  v_deadline timestamptz;
  v_snapshot jsonb;
  v_question_count integer;
begin
  if pg_catalog.length(pg_catalog.btrim(coalesce(p_idempotency_key, ''))) not between 8 and 128 then
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
    and s.supervision_mode in ('None', 'Standard');
  if not found then
    raise exception 'PUBLIC_QUIZ_SESSION_NOT_ACTIVE' using errcode = 'P0002';
  end if;
  if v_session.admission_mode = 'ClassMembersOnly' then
    if not exists (
      select 1 from public.class_members m
      where m.class_id = v_session.class_id
        and m.organization_id = v_profile.organization_id
        and m.user_id = v_profile.id) then
      raise exception 'CLASS_MEMBERSHIP_REQUIRED' using errcode = '42501';
    end if;
    if not exists (
      select 1 from public.public_class_assignments a
      where a.organization_id = v_profile.organization_id
        and a.class_id = v_session.class_id
        and a.exam_id = v_session.exam_id
        and (a.available_from is null or a.available_from <= pg_catalog.now())
        and (a.available_until is null or a.available_until >= pg_catalog.now())) then
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
  
  if v_session.supervision_mode = 'Standard' then
    if not exists (
      select 1 from public.public_device_connections d
      where d.session_id = p_session_id
        and d.participant_id = v_participant.id
        and d.organization_id = v_profile.organization_id
        and d.user_id = v_profile.id
        and d.connection_state in ('Online','Degraded')
        and d.policy_state = 'Applied'
        and (d.policy_lease_expires_at is null or d.policy_lease_expires_at > pg_catalog.now())) then
      raise exception 'SUPERVISION_NOT_READY' using errcode = '42501';
    end if;
  end if;

  select a.* into v_attempt
  from public.quiz_attempts a
  where a.session_id = p_session_id
    and a.participant_id = v_participant.id
    and a.organization_id = v_profile.organization_id
  for update;
  if found and private.is_public_quiz_attempt_snapshot_valid(
      v_attempt.snapshot_json,
      v_attempt.organization_id,
      v_session.exam_id,
      v_attempt.exam_version) then
    if v_attempt.exam_version <> v_session.exam_version then
      raise exception 'QUIZ_ATTEMPT_SNAPSHOT_INVALID' using errcode = '55000';
    end if;
    return v_attempt.id;
  end if;
  if found and (
       v_attempt.status <> 'InProgress'
       or v_attempt.finalized_at is not null
       or v_attempt.grading_status <> 'InProgress'
       or v_attempt.auto_score is not null
       or v_attempt.score is not null
       or v_attempt.graded_at is not null
       or v_attempt.returned_at is not null
       or v_attempt.exam_version <> v_session.exam_version
       or exists (
         select 1 from public.quiz_answers ans
         where ans.attempt_id = v_attempt.id
           and ans.organization_id = v_attempt.organization_id)) then
    raise exception 'QUIZ_ATTEMPT_SNAPSHOT_INVALID' using errcode = '55000';
  end if;

  if v_session.started_at is null then
    raise exception 'SESSION_NOT_STARTED' using errcode = '55000';
  end if;
  v_deadline := v_session.started_at
    + pg_catalog.make_interval(mins => (
      select e.duration_minutes
      from public.exams e
      where e.id = v_session.exam_id
        and e.organization_id = v_session.organization_id)
      + greatest(v_participant.extra_time_minutes, 0));

  select pg_catalog.count(*) into v_question_count
  from public.quiz_questions q
  where q.exam_id = v_session.exam_id
    and q.version = v_session.exam_version
    and q.organization_id = v_profile.organization_id;
  if v_question_count = 0 then
    raise exception 'QUIZ_HAS_NO_QUESTIONS' using errcode = '55000';
  end if;
  if v_question_count > 500 then
    raise exception 'QUIZ_QUESTION_GRAPH_INVALID' using errcode = '55000';
  end if;
  with ranked as (
    select q.*,
      pg_catalog.row_number() over (order by q.sort_order, q.id) as score_rank
    from public.quiz_questions q
    where q.exam_id = v_session.exam_id
      and q.version = v_session.exam_version
      and q.organization_id = v_profile.organization_id)
  select pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object(
      'id', q.id,
      'sortOrder', q.sort_order,
      'questionText', q.question_text,
      'points', (
        (1000 / v_question_count)
        + case when q.score_rank <= (1000 % v_question_count) then 1 else 0 end
      ) / 100.0,
      'multiple', q.multiple,
      'choices', (
        select coalesce(pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object(
          'id', c.id,
          'sortOrder', c.sort_order,
          'choiceText', c.choice_text)
          order by c.sort_order), '[]'::jsonb)
        from public.quiz_choices c
        where c.question_id = q.id
          and c.organization_id = v_profile.organization_id))
      order by q.sort_order)
  into v_snapshot
  from ranked q;
  if not private.is_public_quiz_attempt_snapshot_valid(
      v_snapshot,
      v_profile.organization_id,
      v_session.exam_id,
      v_session.exam_version) then
    raise exception 'QUIZ_QUESTION_GRAPH_INVALID' using errcode = '55000';
  end if;

  if v_attempt.id is not null then
    update public.quiz_attempts
    set snapshot_json = v_snapshot,
        cloud_version = private.next_public_cloud_version(),
        updated_at = pg_catalog.now()
    where id = v_attempt.id
      and organization_id = v_attempt.organization_id;
    return v_attempt.id;
  end if;

  v_attempt_id := pg_catalog.gen_random_uuid();
  insert into public.quiz_attempts(
    id, organization_id, session_id, participant_id, exam_version, result_policy,
    status, started_at, deadline_at, max_score, snapshot_json,
    finalize_idempotency_key, source_mode, cloud_version, created_at, updated_at)
  values (
    v_attempt_id, v_profile.organization_id, p_session_id, v_participant.id,
    v_session.exam_version, v_session.quiz_result_policy, 'InProgress',
    pg_catalog.now(), v_deadline, 10.00, v_snapshot, null, 'PublicCloud',
    private.next_public_cloud_version(), pg_catalog.now(), pg_catalog.now());
  return v_attempt_id;
end
$function$;

revoke all on function public.start_public_quiz_attempt(uuid,text)
  from public, anon, authenticated, service_role;
grant execute on function public.start_public_quiz_attempt(uuid,text)
  to authenticated;

update public.examtransfer_cloud_meta
set schema_version = 30, updated_at = now()
where id = 1;

commit;
