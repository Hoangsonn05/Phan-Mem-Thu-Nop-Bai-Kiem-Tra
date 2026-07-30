begin;

alter table public.quiz_attempts
  add column if not exists auto_score numeric(10,2),
  add column if not exists grading_status text not null default 'InProgress',
  add column if not exists general_comment text,
  add column if not exists grader_id uuid,
  add column if not exists graded_at timestamptz,
  add column if not exists returned_at timestamptz;

update public.quiz_attempts
set auto_score = score,
    max_score = 10.00,
    grading_status = case when status = 'Finalized' then 'Graded' else 'InProgress' end,
    graded_at = case when status = 'Finalized' then finalized_at else null end
where auto_score is null;

alter table public.quiz_attempts
  drop constraint if exists quiz_attempts_grading_status_check,
  add constraint quiz_attempts_grading_status_check
    check (grading_status in ('NotGraded','InProgress','Graded','Returned')),
  drop constraint if exists quiz_attempts_score10_check,
  add constraint quiz_attempts_score10_check
    check (
      max_score = 10.00
      and (auto_score is null or auto_score between 0 and 10.00)
      and (score is null or score between 0 and 10.00)
    );

create or replace function public.get_public_quiz_attempt(p_attempt_id uuid)
returns jsonb
language plpgsql
volatile
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
  v_result jsonb;
begin
  select jsonb_build_object(
    'id', a.id,
    'sessionId', a.session_id,
    'participantId', a.participant_id,
    'status', a.status,
    'examVersion', a.exam_version,
    'resultPolicy', a.result_policy,
    'startedAtUtc', a.started_at,
    'deadlineUtc', a.deadline_at,
    'finalizedAtUtc', a.finalized_at,
    'scoreVisible', a.status = 'Finalized'
      and (a.result_policy = 'ShowAfterSubmission' or a.returned_at is not null),
    'score', case
      when a.status = 'Finalized'
       and (a.result_policy = 'ShowAfterSubmission' or a.returned_at is not null)
      then a.score else null end,
    'maxScore', 10.00,
    'questions', a.snapshot_json,
    'answers', coalesce((
      select jsonb_agg(jsonb_build_object(
        'questionId', ans.question_id,
        'choiceIds', ans.choice_ids,
        'revision', ans.revision,
        'clientUpdatedAtUtc', ans.client_updated_at)
        order by ans.question_id)
      from public.quiz_answers ans
      where ans.attempt_id = a.id
        and ans.organization_id = a.organization_id), '[]'::jsonb))
  into v_result
  from public.quiz_attempts a
  join public.session_participants p
    on p.id = a.participant_id
   and p.organization_id = a.organization_id
   and p.user_id = v_profile.id
  where a.id = p_attempt_id
    and a.organization_id = v_profile.organization_id
    and a.source_mode = 'PublicCloud';
  if v_result is null then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_NOT_FOUND' using errcode = 'P0002';
  end if;
  return v_result;
end
$function$;
revoke all on function public.get_public_quiz_attempt(uuid) from public, anon;
grant execute on function public.get_public_quiz_attempt(uuid) to authenticated;

drop function if exists public.get_teacher_quiz_attempts(uuid);
create function public.get_teacher_quiz_attempts(p_session_id uuid)
returns table (
  id uuid,
  participant_id uuid,
  status text,
  exam_version integer,
  result_policy text,
  started_at timestamptz,
  deadline_at timestamptz,
  finalized_at timestamptz,
  auto_score numeric,
  score numeric,
  max_score numeric,
  grading_status text,
  general_comment text,
  grader_id uuid,
  graded_at timestamptz,
  returned_at timestamptz)
language plpgsql
volatile
security definer
set search_path = ''
as $function$
declare
  v_session public.exam_sessions%rowtype := private.require_public_session_teacher(p_session_id);
begin
  return query
  select a.id, a.participant_id, a.status, a.exam_version, a.result_policy,
         a.started_at, a.deadline_at, a.finalized_at, a.auto_score, a.score,
         10.00::numeric, a.grading_status, a.general_comment, a.grader_id,
         a.graded_at, a.returned_at
  from public.quiz_attempts a
  where a.session_id = v_session.id
    and a.organization_id = v_session.organization_id
  order by a.started_at desc, a.id desc;
end
$function$;
revoke all on function public.get_teacher_quiz_attempts(uuid) from public, anon;
grant execute on function public.get_teacher_quiz_attempts(uuid) to authenticated;

create or replace function public.get_public_quiz_attempt_review(p_attempt_id uuid)
returns jsonb
language plpgsql
volatile
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
  v_attempt public.quiz_attempts%rowtype;
  v_questions jsonb;
begin
  select a.* into v_attempt
  from public.quiz_attempts a
  join public.session_participants p
    on p.id = a.participant_id
   and p.organization_id = a.organization_id
   and p.user_id = v_profile.id
  where a.id = p_attempt_id
    and a.organization_id = v_profile.organization_id
    and a.source_mode = 'PublicCloud'
    and a.status = 'Finalized';
  if not found then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_NOT_FOUND' using errcode = 'P0002';
  end if;

  if v_attempt.returned_at is null then
    v_questions := v_attempt.snapshot_json;
  else
    select coalesce(
      jsonb_agg(
        q.value || jsonb_build_object(
          'choices',
          (select coalesce(
             jsonb_agg(
               c.value || jsonb_build_object(
                 'correct', coalesce(qc.is_correct, false))),
             '[]'::jsonb)
           from jsonb_array_elements(q.value -> 'choices') c(value)
           left join public.quiz_choices qc
             on qc.id = (c.value ->> 'id')::uuid
            and qc.question_id = (q.value ->> 'id')::uuid
            and qc.organization_id = v_attempt.organization_id))),
      '[]'::jsonb)
    into v_questions
    from jsonb_array_elements(v_attempt.snapshot_json) q(value);
  end if;

  return jsonb_build_object(
    'attemptId', v_attempt.id,
    'scoreVisible', v_attempt.result_policy = 'ShowAfterSubmission'
      or v_attempt.returned_at is not null,
    'score', case
      when v_attempt.result_policy = 'ShowAfterSubmission'
        or v_attempt.returned_at is not null then v_attempt.score
      else null end,
    'maxScore', 10.00,
    'correctAnswersVisible', v_attempt.returned_at is not null,
    'generalComment', case when v_attempt.returned_at is not null
      then v_attempt.general_comment else null end,
    'questions', v_questions,
    'answers', coalesce((
      select jsonb_agg(jsonb_build_object(
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
revoke all on function public.get_public_quiz_attempt_review(uuid) from public, anon;
grant execute on function public.get_public_quiz_attempt_review(uuid) to authenticated;

create or replace function private.notify_public_quiz_grade_returned()
returns trigger
language plpgsql
security definer
set search_path = ''
as $function$
begin
  if new.source_mode = 'PublicCloud'
     and old.returned_at is null
     and new.returned_at is not null then
    perform realtime.send(
      jsonb_build_object(
        'attemptId', new.id,
        'sessionId', new.session_id,
        'score', new.score,
        'maxScore', 10.00,
        'returnedAtUtc', new.returned_at),
      'QuizGradeReturned',
      'exam-session:' || new.session_id::text,
      true);
  end if;
  return new;
end
$function$;

drop trigger if exists quiz_attempts_notify_grade_returned
  on public.quiz_attempts;
create trigger quiz_attempts_notify_grade_returned
after update of returned_at on public.quiz_attempts
for each row execute function private.notify_public_quiz_grade_returned();

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
  v_question_count integer;
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
    and s.status in ('InProgress','Paused')
    and s.delivery_type = 'MultipleChoice'
    and s.supervision_mode = 'Standard';
  if not found then raise exception 'PUBLIC_QUIZ_SESSION_NOT_ACTIVE' using errcode = 'P0002'; end if;
  if v_session.admission_mode = 'ClassMembersOnly' then
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
  end if;
  select * into v_participant
  from public.session_participants
  where session_id = p_session_id and user_id = v_profile.id
    and organization_id = v_profile.organization_id
    and status = 'Approved' and source_mode = 'PublicCloud';
  if not found then raise exception 'APPROVED_PARTICIPANT_REQUIRED' using errcode = '42501'; end if;
  if not exists (
    select 1 from public.public_device_connections d
    where d.session_id = p_session_id
      and d.participant_id = v_participant.id
      and d.organization_id = v_profile.organization_id
      and d.user_id = v_profile.id
      and d.connection_state in ('Online','Degraded')
      and d.policy_state = 'Applied'
      and (d.policy_lease_expires_at is null or d.policy_lease_expires_at > now())
  ) then raise exception 'SUPERVISION_NOT_READY' using errcode = '42501'; end if;

  select id into v_attempt_id from public.quiz_attempts
  where session_id = p_session_id and participant_id = v_participant.id;
  if found then return v_attempt_id; end if;
  if v_session.started_at is null then raise exception 'SESSION_NOT_STARTED' using errcode = '55000'; end if;
  v_deadline := v_session.started_at
    + make_interval(mins => (
      select duration_minutes from public.exams
      where id = v_session.exam_id and organization_id = v_session.organization_id)
      + greatest(v_participant.extra_time_minutes, 0));

  select count(*) into v_question_count
  from public.quiz_questions q
  where q.exam_id = v_session.exam_id
    and q.version = v_session.exam_version
    and q.organization_id = v_profile.organization_id;
  if v_question_count not between 1 and 500 then
    raise exception 'QUIZ_HAS_NO_QUESTIONS' using errcode = '55000';
  end if;
  with ranked as (
    select q.*,
      row_number() over (order by q.sort_order, q.id) as score_rank
    from public.quiz_questions q
    where q.exam_id = v_session.exam_id
      and q.version = v_session.exam_version
      and q.organization_id = v_profile.organization_id)
  select jsonb_agg(jsonb_build_object(
      'id', q.id,
      'sortOrder', q.sort_order,
      'questionText', q.question_text,
      'points', (
        (1000 / v_question_count)
        + case when q.score_rank <= (1000 % v_question_count) then 1 else 0 end
      ) / 100.0,
      'multiple', q.multiple,
      'choices', (select coalesce(jsonb_agg(jsonb_build_object(
        'id', c.id, 'sortOrder', c.sort_order, 'choiceText', c.choice_text)
        order by c.sort_order), '[]'::jsonb)
        from public.quiz_choices c where c.question_id = q.id))
      order by q.sort_order)
  into v_snapshot
  from ranked q;

  v_attempt_id := gen_random_uuid();
  insert into public.quiz_attempts(
    id, organization_id, session_id, participant_id, exam_version, result_policy,
    status, started_at, deadline_at, max_score, snapshot_json,
    finalize_idempotency_key, source_mode, cloud_version, created_at, updated_at)
  values (
    v_attempt_id, v_profile.organization_id, p_session_id, v_participant.id,
    v_session.exam_version, v_session.quiz_result_policy, 'InProgress',
    now(), v_deadline, 10.00, v_snapshot, null, 'PublicCloud',
    private.next_public_cloud_version(), now(), now());
  return v_attempt_id;
end
$function$;
revoke all on function public.start_public_quiz_attempt(uuid,text) from public, anon;
grant execute on function public.start_public_quiz_attempt(uuid,text) to authenticated;

create or replace function public.finalize_public_quiz_attempt(
  p_attempt_id uuid,
  p_idempotency_key text)
returns jsonb
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
  where a.id = p_attempt_id
    and a.organization_id = v_profile.organization_id
    and a.source_mode = 'PublicCloud'
    and p.user_id = v_profile.id
    and p.organization_id = a.organization_id
    and s.access_mode = 'PublicCloud';
  if not found then raise exception 'PUBLIC_QUIZ_ATTEMPT_NOT_FOUND' using errcode = 'P0002'; end if;
  if v_attempt.status = 'Finalized' then
    if v_attempt.finalize_idempotency_key <> btrim(p_idempotency_key) then
      raise exception 'QUIZ_ATTEMPT_ALREADY_FINALIZED' using errcode = '55000';
    end if;
    return public.get_public_quiz_attempt(p_attempt_id);
  end if;
  if v_attempt.status <> 'InProgress' then raise exception 'QUIZ_ATTEMPT_CLOSED' using errcode = '55000'; end if;
  for v_row in
    select q.id,
      (
        (1000 / count(*) over ())
        + case when row_number() over (order by q.sort_order, q.id)
                    <= (1000 % count(*) over ())
               then 1 else 0 end
      ) / 100.0 as points,
      coalesce((select array_agg(c.id order by c.id) from public.quiz_choices c
                where c.question_id = q.id and c.is_correct), array[]::uuid[]) as correct_ids,
      coalesce((select array_agg(x.value::uuid order by x.value::uuid)
                from public.quiz_answers a,
                     lateral jsonb_array_elements_text(a.choice_ids) x(value)
                where a.attempt_id = p_attempt_id and a.question_id = q.id), array[]::uuid[]) as selected_ids
    from public.quiz_questions q
    where q.exam_id = (select exam_id from public.exam_sessions where id = v_attempt.session_id)
      and q.version = v_attempt.exam_version
      and q.organization_id = v_profile.organization_id
  loop
    if v_row.correct_ids = v_row.selected_ids then v_score := v_score + v_row.points; end if;
  end loop;
  update public.quiz_attempts
  set status = 'Finalized',
      finalized_at = now(),
      auto_score = v_score,
      score = v_score,
      max_score = 10.00,
      grading_status = 'Graded',
      graded_at = now(),
      finalize_idempotency_key = btrim(p_idempotency_key),
      cloud_version = private.next_public_cloud_version(),
      updated_at = now()
  where id = p_attempt_id;
  return public.get_public_quiz_attempt(p_attempt_id);
end
$function$;
revoke all on function public.finalize_public_quiz_attempt(uuid,text) from public, anon;
grant execute on function public.finalize_public_quiz_attempt(uuid,text) to authenticated;

update public.examtransfer_cloud_meta
set schema_version = 22,
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
