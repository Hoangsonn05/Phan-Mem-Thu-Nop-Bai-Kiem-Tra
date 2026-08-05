begin;

create or replace function private.is_public_quiz_attempt_snapshot_valid(
  p_snapshot jsonb,
  p_organization_id uuid,
  p_exam_id uuid,
  p_exam_version integer)
returns boolean
language plpgsql
stable
security invoker
set search_path = ''
as $function$
declare
  v_question jsonb;
  v_choice jsonb;
  v_question_row public.quiz_questions%rowtype;
  v_question_id uuid;
  v_choice_id uuid;
  v_question_order integer;
  v_choice_order integer;
  v_points numeric;
  v_multiple boolean;
  v_total_points numeric := 0;
  v_seen_question_ids uuid[] := '{}'::uuid[];
  v_seen_choice_ids uuid[] := '{}'::uuid[];
  v_seen_question_orders integer[] := '{}'::integer[];
  v_seen_choice_orders integer[];
  v_snapshot_count integer;
  v_authoritative_count integer;
  v_authoritative_choice_count integer;
  v_correct_choice_count integer;
begin
  if pg_catalog.jsonb_typeof(p_snapshot) <> 'array' then
    return false;
  end if;
  v_snapshot_count := pg_catalog.jsonb_array_length(p_snapshot);
  if v_snapshot_count not between 1 and 500 then
    return false;
  end if;
  select pg_catalog.count(*) into v_authoritative_count
  from public.quiz_questions q
  where q.organization_id = p_organization_id
    and q.exam_id = p_exam_id
    and q.version = p_exam_version;
  if v_authoritative_count <> v_snapshot_count then
    return false;
  end if;

  for v_question in
    select value from pg_catalog.jsonb_array_elements(p_snapshot)
  loop
    if pg_catalog.jsonb_typeof(v_question) <> 'object'
       or pg_catalog.jsonb_typeof(v_question -> 'id') <> 'string'
       or pg_catalog.jsonb_typeof(v_question -> 'sortOrder') <> 'number'
       or pg_catalog.jsonb_typeof(v_question -> 'questionText') <> 'string'
       or pg_catalog.jsonb_typeof(v_question -> 'points') <> 'number'
       or pg_catalog.jsonb_typeof(v_question -> 'multiple') <> 'boolean'
       or pg_catalog.jsonb_typeof(v_question -> 'choices') <> 'array'
       or exists (
         select 1
         from pg_catalog.jsonb_object_keys(v_question) key
         where pg_catalog.lower(key) in ('correct','iscorrect')) then
      return false;
    end if;
    begin
      v_question_id := (v_question ->> 'id')::uuid;
      v_question_order := (v_question ->> 'sortOrder')::integer;
      v_points := (v_question ->> 'points')::numeric;
      v_multiple := (v_question ->> 'multiple')::boolean;
    exception when others then
      return false;
    end;
    if v_question_id = '00000000-0000-0000-0000-000000000000'::uuid
       or v_question_id = any(v_seen_question_ids)
       or v_question_order <= 0
       or v_question_order = any(v_seen_question_orders)
       or pg_catalog.length(pg_catalog.btrim(v_question ->> 'questionText')) not between 1 and 5000
       or v_points <= 0
       or v_points <> pg_catalog.round(v_points, 2)
       or pg_catalog.jsonb_array_length(v_question -> 'choices') not between 2 and 10 then
      return false;
    end if;
    v_seen_question_ids := pg_catalog.array_append(v_seen_question_ids, v_question_id);
    v_seen_question_orders := pg_catalog.array_append(v_seen_question_orders, v_question_order);
    v_total_points := v_total_points + v_points;

    select q.* into v_question_row
    from public.quiz_questions q
    where q.id = v_question_id
      and q.organization_id = p_organization_id
      and q.exam_id = p_exam_id
      and q.version = p_exam_version;
    if not found
       or v_question_row.sort_order <> v_question_order
       or v_question_row.question_text <> (v_question ->> 'questionText')
       or v_question_row.multiple <> v_multiple then
      return false;
    end if;
    select pg_catalog.count(*),
           pg_catalog.count(*) filter (where c.is_correct)
      into v_authoritative_choice_count, v_correct_choice_count
    from public.quiz_choices c
    where c.question_id = v_question_id
      and c.organization_id = p_organization_id;
    if v_authoritative_choice_count <> pg_catalog.jsonb_array_length(v_question -> 'choices')
       or v_authoritative_choice_count not between 2 and 10
       or v_correct_choice_count = 0
       or (not v_multiple and v_correct_choice_count <> 1) then
      return false;
    end if;

    v_seen_choice_orders := '{}'::integer[];
    for v_choice in
      select value from pg_catalog.jsonb_array_elements(v_question -> 'choices')
    loop
      if pg_catalog.jsonb_typeof(v_choice) <> 'object'
         or pg_catalog.jsonb_typeof(v_choice -> 'id') <> 'string'
         or pg_catalog.jsonb_typeof(v_choice -> 'sortOrder') <> 'number'
         or pg_catalog.jsonb_typeof(v_choice -> 'choiceText') <> 'string'
         or exists (
           select 1
           from pg_catalog.jsonb_object_keys(v_choice) key
           where pg_catalog.lower(key) in ('correct','iscorrect')) then
        return false;
      end if;
      begin
        v_choice_id := (v_choice ->> 'id')::uuid;
        v_choice_order := (v_choice ->> 'sortOrder')::integer;
      exception when others then
        return false;
      end;
      if v_choice_id = '00000000-0000-0000-0000-000000000000'::uuid
         or v_choice_id = any(v_seen_choice_ids)
         or v_choice_order <= 0
         or v_choice_order = any(v_seen_choice_orders)
         or pg_catalog.length(pg_catalog.btrim(v_choice ->> 'choiceText')) not between 1 and 5000
         or not exists (
           select 1
           from public.quiz_choices c
           where c.id = v_choice_id
             and c.question_id = v_question_id
             and c.organization_id = p_organization_id
             and c.sort_order = v_choice_order
             and c.choice_text = (v_choice ->> 'choiceText')) then
        return false;
      end if;
      v_seen_choice_ids := pg_catalog.array_append(v_seen_choice_ids, v_choice_id);
      v_seen_choice_orders := pg_catalog.array_append(v_seen_choice_orders, v_choice_order);
    end loop;
  end loop;
  return v_total_points = 10.00;
exception when others then
  return false;
end
$function$;

revoke all on function private.is_public_quiz_attempt_snapshot_valid(jsonb,uuid,uuid,integer)
  from public, anon, authenticated, service_role;

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
    and s.supervision_mode = 'Standard';
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

create or replace function public.get_public_quiz_attempt(p_attempt_id uuid)
returns jsonb
language plpgsql
volatile
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
  v_attempt public.quiz_attempts%rowtype;
  v_session public.exam_sessions%rowtype;
begin
  select a.* into v_attempt
  from public.quiz_attempts a
  join public.session_participants p
    on p.id = a.participant_id
   and p.session_id = a.session_id
   and p.organization_id = a.organization_id
   and p.user_id = v_profile.id
  join public.exam_sessions s
    on s.id = a.session_id
   and s.organization_id = a.organization_id
   and s.access_mode = 'PublicCloud'
  where a.id = p_attempt_id
    and a.organization_id = v_profile.organization_id
    and a.source_mode = 'PublicCloud';
  if not found then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_NOT_FOUND' using errcode = 'P0002';
  end if;
  select s.* into v_session
  from public.exam_sessions s
  where s.id = v_attempt.session_id
    and s.organization_id = v_attempt.organization_id
    and s.access_mode = 'PublicCloud';
  if not found then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_NOT_FOUND' using errcode = 'P0002';
  end if;
  if v_attempt.exam_version <> v_session.exam_version
     or not private.is_public_quiz_attempt_snapshot_valid(
       v_attempt.snapshot_json,
       v_attempt.organization_id,
       v_session.exam_id,
       v_attempt.exam_version) then
    raise exception 'QUIZ_ATTEMPT_SNAPSHOT_INVALID' using errcode = '55000';
  end if;
  return pg_catalog.jsonb_build_object(
    'id', v_attempt.id,
    'sessionId', v_attempt.session_id,
    'participantId', v_attempt.participant_id,
    'status', v_attempt.status,
    'examVersion', v_attempt.exam_version,
    'resultPolicy', v_attempt.result_policy,
    'startedAtUtc', v_attempt.started_at,
    'deadlineUtc', v_attempt.deadline_at,
    'finalizedAtUtc', v_attempt.finalized_at,
    'scoreVisible', v_attempt.status = 'Finalized'
      and v_attempt.grading_status = 'Returned'
      and v_attempt.returned_at is not null,
    'score', case when v_attempt.status = 'Finalized'
      and v_attempt.grading_status = 'Returned'
      and v_attempt.returned_at is not null
      then v_attempt.score else null end,
    'maxScore', v_attempt.max_score,
    'questions', v_attempt.snapshot_json,
    'answers', coalesce((
      select pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object(
        'questionId', ans.question_id,
        'choiceIds', ans.choice_ids,
        'revision', ans.revision,
        'clientUpdatedAtUtc', ans.client_updated_at)
        order by ans.question_id)
      from public.quiz_answers ans
      where ans.attempt_id = v_attempt.id
        and ans.organization_id = v_attempt.organization_id), '[]'::jsonb));
end
$function$;

revoke all on function public.start_public_quiz_attempt(uuid,text)
  from public, anon, authenticated, service_role;
revoke all on function public.get_public_quiz_attempt(uuid)
  from public, anon, authenticated, service_role;
grant execute on function public.start_public_quiz_attempt(uuid,text)
  to authenticated;
grant execute on function public.get_public_quiz_attempt(uuid)
  to authenticated;

commit;
