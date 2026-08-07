create or replace function private.calculate_public_quiz_grade(p_attempt_id uuid)
returns jsonb
language plpgsql
stable
security definer
set search_path = ''
as $function$
declare
  v_attempt public.quiz_attempts%rowtype;
  v_session public.exam_sessions%rowtype;
  v_snapshot_question jsonb;
  v_snapshot_choice jsonb;
  v_answer public.quiz_answers%rowtype;
  v_question public.quiz_questions%rowtype;
  v_question_id uuid;
  v_question_points numeric(10,2);
  v_choice_id uuid;
  v_seen_questions uuid[] := array[]::uuid[];
  v_snapshot_choice_ids uuid[];
  v_selected_ids uuid[];
  v_correct_ids uuid[];
  v_total integer := 0;
  v_answered integer := 0;
  v_correct integer := 0;
  v_score numeric(10,2) := 0;
  v_max_score numeric(10,2) := 0;
begin
  select a.* into v_attempt
  from public.quiz_attempts a
  where a.id = p_attempt_id;
  if not found then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_NOT_FOUND' using errcode = 'P0002';
  end if;
  select s.* into v_session
  from public.exam_sessions s
  where s.id = v_attempt.session_id
    and s.organization_id = v_attempt.organization_id;
  if not found then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_GRAPH_INVALID' using errcode = '22023';
  end if;
  if v_attempt.source_mode <> 'PublicCloud'
     or v_session.access_mode <> 'PublicCloud'
     or v_session.delivery_type <> 'MultipleChoice'
     or v_attempt.status not in ('InProgress','Finalized')
     or v_attempt.exam_version <> v_session.exam_version
     or not exists (
       select 1
       from public.exams e
       where e.id = v_session.exam_id
         and e.organization_id = v_session.organization_id
         and e.delivery_type = 'MultipleChoice')
     or not exists (
       select 1
       from public.session_participants p
       where p.id = v_attempt.participant_id
         and p.session_id = v_attempt.session_id
         and p.organization_id = v_attempt.organization_id
         and p.source_mode = 'PublicCloud') then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_GRAPH_INVALID' using errcode = '22023';
  end if;
  if pg_catalog.jsonb_typeof(v_attempt.snapshot_json) <> 'array'
     or pg_catalog.jsonb_array_length(v_attempt.snapshot_json) = 0 then
    raise exception 'PUBLIC_QUIZ_SNAPSHOT_INVALID' using errcode = '22023';
  end if;

  for v_snapshot_question in
    select value from pg_catalog.jsonb_array_elements(v_attempt.snapshot_json)
  loop
    begin
      v_question_id := (v_snapshot_question ->> 'id')::uuid;
      v_question_points := (v_snapshot_question ->> 'points')::numeric;
    exception when others then
      raise exception 'PUBLIC_QUIZ_SNAPSHOT_QUESTION_INVALID' using errcode = '22023';
    end;
    if v_question_id = any(v_seen_questions) then
      raise exception 'PUBLIC_QUIZ_SNAPSHOT_QUESTION_DUPLICATE' using errcode = '22023';
    end if;
    v_seen_questions := pg_catalog.array_append(v_seen_questions, v_question_id);

    select q.* into v_question
    from public.quiz_questions q
    where q.id = v_question_id
      and q.exam_id = v_session.exam_id
      and q.organization_id = v_attempt.organization_id
      and q.version = v_attempt.exam_version;
    if not found
       or v_question_points <= 0
       or v_question_points <> pg_catalog.round(v_question_points, 2)
       or v_question.multiple is distinct from
          coalesce((v_snapshot_question ->> 'multiple')::boolean, false)
       or pg_catalog.jsonb_typeof(v_snapshot_question -> 'choices') <> 'array' then
      raise exception 'PUBLIC_QUIZ_SNAPSHOT_QUESTION_MISMATCH' using errcode = '22023';
    end if;

    v_snapshot_choice_ids := array[]::uuid[];
    for v_snapshot_choice in
      select value from pg_catalog.jsonb_array_elements(v_snapshot_question -> 'choices')
    loop
      begin
        v_choice_id := (v_snapshot_choice ->> 'id')::uuid;
      exception when others then
        raise exception 'PUBLIC_QUIZ_SNAPSHOT_CHOICE_INVALID' using errcode = '22023';
      end;
      if v_choice_id = any(v_snapshot_choice_ids)
         or not exists (
           select 1 from public.quiz_choices c
           where c.id = v_choice_id
             and c.question_id = v_question_id
             and c.organization_id = v_attempt.organization_id) then
        raise exception 'PUBLIC_QUIZ_SNAPSHOT_CHOICE_MISMATCH' using errcode = '22023';
      end if;
      v_snapshot_choice_ids := pg_catalog.array_append(v_snapshot_choice_ids, v_choice_id);
    end loop;
    if coalesce(pg_catalog.array_length(v_snapshot_choice_ids, 1), 0) = 0
       or coalesce(pg_catalog.array_length(v_snapshot_choice_ids, 1), 0) <>
          (select pg_catalog.count(*) from public.quiz_choices c
           where c.question_id = v_question_id
             and c.organization_id = v_attempt.organization_id) then
      raise exception 'PUBLIC_QUIZ_SNAPSHOT_CHOICE_COUNT_MISMATCH' using errcode = '22023';
    end if;

    select coalesce(pg_catalog.array_agg(c.id order by c.id), array[]::uuid[])
      into v_correct_ids
    from public.quiz_choices c
    where c.question_id = v_question_id
      and c.organization_id = v_attempt.organization_id
      and c.is_correct;
    if coalesce(pg_catalog.array_length(v_correct_ids, 1), 0) = 0 then
      raise exception 'PUBLIC_QUIZ_CORRECT_CHOICE_MISSING' using errcode = '22023';
    end if;

    select a.* into v_answer
    from public.quiz_answers a
    where a.attempt_id = p_attempt_id
      and a.question_id = v_question_id;
    v_selected_ids := array[]::uuid[];
    if found then
      if v_answer.organization_id <> v_attempt.organization_id
         or pg_catalog.jsonb_typeof(v_answer.choice_ids) <> 'array' then
        raise exception 'PUBLIC_QUIZ_ANSWER_GRAPH_INVALID' using errcode = '22023';
      end if;
      for v_snapshot_choice in
        select value from pg_catalog.jsonb_array_elements(v_answer.choice_ids)
      loop
        begin
          v_choice_id := (v_snapshot_choice #>> '{}')::uuid;
        exception when others then
          raise exception 'PUBLIC_QUIZ_ANSWER_CHOICE_INVALID' using errcode = '22023';
        end;
        if v_choice_id = any(v_selected_ids)
           or not exists (
             select 1 from public.quiz_choices c
             where c.id = v_choice_id
               and c.question_id = v_question_id
               and c.organization_id = v_attempt.organization_id) then
          raise exception 'PUBLIC_QUIZ_ANSWER_CHOICE_MISMATCH' using errcode = '22023';
        end if;
        v_selected_ids := pg_catalog.array_append(v_selected_ids, v_choice_id);
      end loop;
      if not v_question.multiple
         and coalesce(pg_catalog.array_length(v_selected_ids, 1), 0) > 1 then
        raise exception 'PUBLIC_QUIZ_ANSWER_MULTIPLE_INVALID' using errcode = '22023';
      end if;
    end if;

    v_total := v_total + 1;
    v_max_score := v_max_score + v_question_points;
    if coalesce(pg_catalog.array_length(v_selected_ids, 1), 0) > 0 then
      v_answered := v_answered + 1;
      if v_selected_ids @> v_correct_ids and v_correct_ids @> v_selected_ids then
        v_correct := v_correct + 1;
        v_score := v_score + v_question_points;
      end if;
    end if;
  end loop;

  if v_total <> (
       select pg_catalog.count(*)
       from public.quiz_questions q
       where q.exam_id = v_session.exam_id
         and q.organization_id = v_attempt.organization_id
         and q.version = v_attempt.exam_version)
     or exists (
       select 1 from public.quiz_answers a
       where a.attempt_id = p_attempt_id
         and (a.organization_id <> v_attempt.organization_id
           or not (a.question_id = any(v_seen_questions)))) then
    raise exception 'PUBLIC_QUIZ_ANSWER_GRAPH_INVALID' using errcode = '22023';
  end if;
  if v_max_score <> 10.00
     or v_attempt.max_score <> v_max_score
     or v_score < 0
     or v_score > v_max_score
     or v_score <> pg_catalog.round(v_score, 2) then
    raise exception 'PUBLIC_QUIZ_SCORE_INVARIANT_INVALID' using errcode = '22023';
  end if;

  return pg_catalog.jsonb_build_object(
    'score', v_score,
    'maxScore', v_max_score,
    'totalQuestions', v_total,
    'answeredQuestions', v_answered,
    'correctCount', v_correct,
    'incorrectCount', v_answered - v_correct,
    'unansweredCount', v_total - v_answered);
end
$function$;

create or replace function public.get_public_quiz_attempt(p_attempt_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
  v_attempt public.quiz_attempts%rowtype;
  v_session public.exam_sessions%rowtype;
  v_calculated jsonb;
  v_score_visible boolean := false;
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
  if v_attempt.status = 'Finalized' then
    v_calculated := private.calculate_public_quiz_grade(p_attempt_id);
    if v_attempt.auto_score is distinct from (v_calculated ->> 'score')::numeric
       or v_attempt.score is distinct from (v_calculated ->> 'score')::numeric
       or v_attempt.max_score is distinct from (v_calculated ->> 'maxScore')::numeric then
      raise exception 'QUIZ_GRADE_NOT_AUTHORITATIVE' using errcode = '22023';
    end if;
    v_score_visible := v_attempt.result_policy = 'ShowAfterSubmission'
      or (v_attempt.grading_status = 'Returned' and v_attempt.returned_at is not null);
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
    'scoreVisible', v_score_visible,
    'score', case when v_score_visible then v_attempt.score else null end,
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

create or replace function public.get_public_quiz_attempt_review(p_attempt_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
  v_attempt public.quiz_attempts%rowtype;
  v_questions jsonb;
  v_calculated jsonb;
  v_returned boolean;
  v_score_visible boolean;
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
    and a.source_mode = 'PublicCloud'
    and a.status = 'Finalized';
  if not found then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_NOT_FOUND' using errcode = 'P0002';
  end if;

  v_calculated := private.calculate_public_quiz_grade(p_attempt_id);
  if v_attempt.auto_score is distinct from (v_calculated ->> 'score')::numeric
     or v_attempt.score is distinct from (v_calculated ->> 'score')::numeric
     or v_attempt.max_score is distinct from (v_calculated ->> 'maxScore')::numeric then
    raise exception 'QUIZ_GRADE_NOT_AUTHORITATIVE' using errcode = '22023';
  end if;

  v_returned := v_attempt.grading_status = 'Returned' and v_attempt.returned_at is not null;
  v_score_visible := v_attempt.result_policy = 'ShowAfterSubmission' or v_returned;
  if not v_returned then
    v_questions := v_attempt.snapshot_json;
  else
    select coalesce(
      pg_catalog.jsonb_agg(
        q.value || pg_catalog.jsonb_build_object(
          'choices',
          (select coalesce(
             pg_catalog.jsonb_agg(
               c.value || pg_catalog.jsonb_build_object(
                 'correct', coalesce(qc.is_correct, false))),
             '[]'::jsonb)
           from pg_catalog.jsonb_array_elements(q.value -> 'choices') c(value)
           left join public.quiz_choices qc
             on qc.id = (c.value ->> 'id')::uuid
            and qc.question_id = (q.value ->> 'id')::uuid
            and qc.organization_id = v_attempt.organization_id))),
      '[]'::jsonb)
    into v_questions
    from pg_catalog.jsonb_array_elements(v_attempt.snapshot_json) q(value);
  end if;

  return pg_catalog.jsonb_build_object(
    'attemptId', v_attempt.id,
    'scoreVisible', v_score_visible,
    'score', case when v_score_visible then v_attempt.score else null end,
    'maxScore', v_attempt.max_score,
    'correctAnswersVisible', v_returned,
    'generalComment', case when v_returned then v_attempt.general_comment else null end,
    'questions', v_questions,
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
  v_calculated jsonb;
  v_score numeric(10,2);
  v_max_score numeric(10,2);
begin
  if pg_catalog.length(pg_catalog.btrim(coalesce(p_idempotency_key, ''))) not between 8 and 128 then
    raise exception 'IDEMPOTENCY_KEY_INVALID' using errcode = '22023';
  end if;
  perform pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(p_attempt_id::text, 0));
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
  for update of a;
  if not found then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_NOT_FOUND' using errcode = 'P0002';
  end if;
  if v_attempt.status = 'Finalized' then
    if v_attempt.finalize_idempotency_key <> pg_catalog.btrim(p_idempotency_key) then
      raise exception 'QUIZ_ATTEMPT_ALREADY_FINALIZED' using errcode = '55000';
    end if;
    return public.get_public_quiz_attempt(p_attempt_id);
  end if;
  if v_attempt.status <> 'InProgress' then
    raise exception 'QUIZ_ATTEMPT_CLOSED' using errcode = '55000';
  end if;

  v_calculated := private.calculate_public_quiz_grade(p_attempt_id);
  v_score := (v_calculated ->> 'score')::numeric;
  v_max_score := (v_calculated ->> 'maxScore')::numeric;

  update public.quiz_attempts
  set status = 'Finalized',
      finalized_at = pg_catalog.now(),
      auto_score = v_score,
      score = v_score,
      max_score = v_max_score,
      grading_status = 'Graded',
      graded_at = pg_catalog.now(),
      finalize_idempotency_key = pg_catalog.btrim(p_idempotency_key),
      cloud_version = private.next_public_cloud_version(),
      updated_at = pg_catalog.now()
  where id = p_attempt_id;

  return public.get_public_quiz_attempt(p_attempt_id);
end
$function$;

create or replace function public.get_student_results(
  p_page_size integer default 50,
  p_cursor_returned_at timestamptz default null,
  p_cursor_result_type text default null,
  p_cursor_result_id uuid default null)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
  v_cursor_type_order integer;
  v_items jsonb;
  v_next_cursor jsonb;
begin
  if p_page_size is null or p_page_size < 1 or p_page_size > 100 then
    raise exception 'STUDENT_RESULTS_PAGE_SIZE_INVALID' using errcode = '22023';
  end if;
  if (p_cursor_returned_at is null) <> (p_cursor_result_type is null)
     or (p_cursor_returned_at is null) <> (p_cursor_result_id is null) then
    raise exception 'STUDENT_RESULTS_CURSOR_INCOMPLETE' using errcode = '22023';
  end if;
  if p_cursor_returned_at is not null then
    v_cursor_type_order := case p_cursor_result_type
      when 'EssayFile' then 1
      when 'Quiz' then 2
      else null
    end;
    if v_cursor_type_order is null then
      raise exception 'STUDENT_RESULTS_CURSOR_TYPE_INVALID' using errcode = '22023';
    end if;
  end if;

  with candidates as (
    select
      1 as result_type_order,
      'EssayFile'::text as result_type,
      submission.id as result_id,
      grade.returned_at as published_at,
      pg_catalog.jsonb_build_object(
        'resultType', 'EssayFile',
        'examId', exam.id,
        'examTitle', exam.title,
        'sessionId', session.id,
        'submissionId', submission.id,
        'attemptId', null,
        'attemptNumber', submission.attempt_number,
        'status', 'Returned',
        'score', grade.score,
        'maxScore', grade.max_score,
        'generalComment', grade.general_comment,
        'returnedAtUtc', grade.returned_at,
        'attachments', coalesce((
          select pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object(
            'attachmentId', attachment.id,
            'fileName', coalesce(
              nullif(pg_catalog.regexp_replace(
                pg_catalog.regexp_replace(attachment.name, E'^.*[\\\\/]', ''),
                '[[:cntrl:]]', '', 'g'), ''),
              'attachment-' || pg_catalog.replace(attachment.id::text, '-', '')),
            'contentType', coalesce(nullif(attachment.mime_type, ''), 'application/octet-stream'),
            'sizeBytes', attachment.size_bytes)
            order by attachment.created_at, attachment.id)
          from public.graded_attachments attachment
          where attachment.grade_id = grade.id
            and attachment.organization_id = grade.organization_id), '[]'::jsonb),
        'quizSummary', null) as payload
    from public.grades grade
    join public.submissions submission
      on submission.id = grade.submission_id
     and submission.organization_id = grade.organization_id
     and submission.source_mode = 'PublicCloud'
     and submission.is_official = true
     and submission.status in ('Submitted', 'LateSubmitted')
    join public.session_participants participant
      on participant.id = submission.participant_id
     and participant.session_id = submission.session_id
     and participant.organization_id = grade.organization_id
     and participant.user_id = v_profile.id
     and participant.source_mode = 'PublicCloud'
    join public.exam_sessions session
      on session.id = submission.session_id
     and session.organization_id = grade.organization_id
     and session.access_mode = 'PublicCloud'
     and session.delivery_type = 'FileSubmission'
    join public.exams exam
      on exam.id = session.exam_id
     and exam.organization_id = grade.organization_id
     and exam.delivery_type = 'FileSubmission'
    where grade.organization_id = v_profile.organization_id
      and grade.status = 'Returned'
      and grade.returned_at is not null

    union all

    select
      2 as result_type_order,
      'Quiz'::text as result_type,
      attempt.id as result_id,
      case
        when attempt.grading_status = 'Returned' and attempt.returned_at is not null
          then attempt.returned_at
        else attempt.finalized_at
      end as published_at,
      pg_catalog.jsonb_build_object(
        'resultType', 'Quiz',
        'examId', exam.id,
        'examTitle', exam.title,
        'sessionId', session.id,
        'submissionId', null,
        'attemptId', attempt.id,
        'attemptNumber', attempt.attempt_number,
        'status', 'Returned',
        'score', attempt.score,
        'maxScore', attempt.max_score,
        'generalComment', case
          when attempt.grading_status = 'Returned' and attempt.returned_at is not null
            then attempt.general_comment
          else null
        end,
        'returnedAtUtc', case
          when attempt.grading_status = 'Returned' and attempt.returned_at is not null
            then attempt.returned_at
          else attempt.finalized_at
        end,
        'startedAtUtc', attempt.started_at,
        'finalizedAtUtc', attempt.finalized_at,
        'durationSeconds', pg_catalog.floor(
          extract(epoch from (attempt.finalized_at - attempt.started_at)))::bigint,
        'attachments', '[]'::jsonb,
        'quizSummary', pg_catalog.jsonb_build_object(
          'totalQuestions', (calculated.value ->> 'totalQuestions')::integer,
          'answeredQuestions', (calculated.value ->> 'answeredQuestions')::integer,
          'correctCount', (calculated.value ->> 'correctCount')::integer,
          'incorrectCount', (calculated.value ->> 'incorrectCount')::integer,
          'unansweredCount', (calculated.value ->> 'unansweredCount')::integer,
          'earnedPoints', (calculated.value ->> 'score')::numeric,
          'maxPoints', (calculated.value ->> 'maxScore')::numeric)) as payload
    from public.quiz_attempts attempt
    join public.session_participants participant
      on participant.id = attempt.participant_id
     and participant.session_id = attempt.session_id
     and participant.organization_id = attempt.organization_id
     and participant.user_id = v_profile.id
     and participant.source_mode = 'PublicCloud'
    join public.exam_sessions session
      on session.id = attempt.session_id
     and session.organization_id = attempt.organization_id
     and session.access_mode = 'PublicCloud'
     and session.delivery_type = 'MultipleChoice'
    join public.exams exam
      on exam.id = session.exam_id
     and exam.organization_id = attempt.organization_id
     and exam.delivery_type = 'MultipleChoice'
    cross join lateral (
      select private.calculate_public_quiz_grade(attempt.id) as value
    ) calculated
    where attempt.organization_id = v_profile.organization_id
      and attempt.source_mode = 'PublicCloud'
      and attempt.status = 'Finalized'
      and attempt.finalized_at is not null
      and attempt.finalized_at >= attempt.started_at
      and (
        attempt.result_policy = 'ShowAfterSubmission'
        or (attempt.grading_status = 'Returned' and attempt.returned_at is not null))
      and attempt.auto_score = (calculated.value ->> 'score')::numeric
      and attempt.score = (calculated.value ->> 'score')::numeric
      and attempt.max_score = (calculated.value ->> 'maxScore')::numeric
  ), page_rows as (
    select *, pg_catalog.row_number() over (
      order by published_at desc, result_type_order, result_id) as ordinal
    from candidates
    where p_cursor_returned_at is null
       or published_at < p_cursor_returned_at
       or (published_at = p_cursor_returned_at and result_type_order > v_cursor_type_order)
       or (published_at = p_cursor_returned_at and result_type_order = v_cursor_type_order
           and result_id > p_cursor_result_id)
    order by published_at desc, result_type_order, result_id
    limit p_page_size + 1
  )
  select
    coalesce(pg_catalog.jsonb_agg(payload order by published_at desc, result_type_order, result_id)
      filter (where ordinal <= p_page_size), '[]'::jsonb),
    case when pg_catalog.count(*) > p_page_size then
      (pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object(
        'returnedAtUtc', published_at,
        'resultType', result_type,
        'resultId', result_id)
        order by published_at desc, result_type_order, result_id) -> (p_page_size - 1))
      else null end
  into v_items, v_next_cursor
  from page_rows;

  return pg_catalog.jsonb_build_object(
    'items', v_items,
    'nextCursor', v_next_cursor);
end
$function$;

revoke all on function private.calculate_public_quiz_grade(uuid)
  from public, anon, authenticated, service_role;
revoke all on function public.finalize_public_quiz_attempt(uuid,text)
  from public, anon, service_role;
revoke all on function public.get_public_quiz_attempt(uuid)
  from public, anon, service_role;
revoke all on function public.get_public_quiz_attempt_review(uuid)
  from public, anon, service_role;
revoke all on function public.get_student_results(integer,timestamptz,text,uuid)
  from public, anon, service_role;
grant execute on function public.finalize_public_quiz_attempt(uuid,text)
  to authenticated;
grant execute on function public.get_public_quiz_attempt(uuid)
  to authenticated;
grant execute on function public.get_public_quiz_attempt_review(uuid)
  to authenticated;
grant execute on function public.get_student_results(integer,timestamptz,text,uuid)
  to authenticated;

update public.examtransfer_cloud_meta
set schema_version = 32,
    updated_at = pg_catalog.now()
where id = 1;
