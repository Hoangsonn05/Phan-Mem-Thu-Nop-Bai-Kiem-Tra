begin;

alter table public.exams
  add column if not exists quiz_result_policy text not null default 'Hidden',
  add column if not exists supervision_mode text not null default 'None';
update public.exams
set quiz_result_policy = case when delivery_type = 'MultipleChoice' then 'Hidden' else 'Hidden' end,
    supervision_mode = case when delivery_type = 'MultipleChoice' then 'Standard' else 'None' end
where quiz_result_policy not in ('Hidden','ShowAfterSubmission')
   or supervision_mode not in ('None','Standard')
   or (delivery_type = 'MultipleChoice' and supervision_mode <> 'Standard');
alter table public.exams drop constraint if exists exams_quiz_result_policy_check;
alter table public.exams add constraint exams_quiz_result_policy_check
  check (quiz_result_policy in ('Hidden','ShowAfterSubmission'));
alter table public.exams drop constraint if exists exams_supervision_mode_check;
alter table public.exams add constraint exams_supervision_mode_check
  check (supervision_mode in ('None','Standard')
    and (delivery_type <> 'MultipleChoice' or supervision_mode = 'Standard'));

alter table public.exam_sessions
  add column if not exists delivery_type text not null default 'FileSubmission',
  add column if not exists supervision_mode text not null default 'None',
  add column if not exists quiz_result_policy text not null default 'Hidden',
  add column if not exists exam_version integer not null default 1;
update public.exam_sessions s
set delivery_type = e.delivery_type,
    supervision_mode = e.supervision_mode,
    quiz_result_policy = e.quiz_result_policy,
    exam_version = e.version
from public.exams e
where e.id = s.exam_id
  and e.organization_id = s.organization_id
  and s.delivery_type = 'FileSubmission'
  and s.supervision_mode = 'None'
  and s.quiz_result_policy = 'Hidden'
  and s.exam_version = 1;
alter table public.exam_sessions drop constraint if exists exam_sessions_workflow_snapshot_check;
alter table public.exam_sessions add constraint exam_sessions_workflow_snapshot_check
  check (delivery_type in ('FileSubmission','MultipleChoice')
    and supervision_mode in ('None','Standard')
    and quiz_result_policy in ('Hidden','ShowAfterSubmission')
    and exam_version > 0
    and (delivery_type <> 'MultipleChoice' or supervision_mode = 'Standard'));

alter table public.quiz_attempts
  add column if not exists result_policy text not null default 'Hidden';
update public.quiz_attempts a
set result_policy = s.quiz_result_policy
from public.exam_sessions s
where s.id = a.session_id
  and s.organization_id = a.organization_id;
alter table public.quiz_attempts drop constraint if exists quiz_attempts_result_policy_check;
alter table public.quiz_attempts add constraint quiz_attempts_result_policy_check
  check (result_policy in ('Hidden','ShowAfterSubmission')
    and (status = 'Finalized' or score is null));

create table if not exists public.quiz_import_sources (
  id uuid primary key,
  organization_id uuid not null references public.organizations(id) on delete restrict,
  exam_id uuid not null references public.exams(id) on delete cascade,
  exam_version integer not null check (exam_version > 0),
  original_name text not null check (length(original_name) between 1 and 260),
  mime_type text not null check (mime_type in (
    'application/pdf',
    'application/vnd.openxmlformats-officedocument.wordprocessingml.document')),
  size_bytes bigint not null check (size_bytes between 1 and 10485760),
  sha256 text not null check (sha256 ~ '^[0-9a-f]{64}$'),
  cloud_object_path text not null check (length(cloud_object_path) between 1 and 1024),
  status text not null default 'Committed' check (status in ('Committed','Archived')),
  created_by uuid not null references public.profiles(id) on delete restrict,
  imported_at timestamptz not null default now(),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (exam_id, exam_version)
);
create index if not exists ix_quiz_import_sources_org_exam
  on public.quiz_import_sources(organization_id, exam_id, exam_version);
alter table public.quiz_import_sources enable row level security;
alter table public.quiz_import_sources force row level security;
drop policy if exists quiz_import_sources_staff_all on public.quiz_import_sources;
create policy quiz_import_sources_staff_all on public.quiz_import_sources
  for all to authenticated
  using (
    organization_id = (select public.current_organization_id())
    and (select public.current_examtransfer_role()) in ('Admin','Teacher'))
  with check (
    organization_id = (select public.current_organization_id())
    and (
      (select public.current_examtransfer_role()) = 'Admin'
      or created_by = (select auth.uid()))
    and (select public.current_examtransfer_role()) in ('Admin','Teacher'));
revoke all on public.quiz_import_sources from public, anon;
grant select, insert, update, delete on public.quiz_import_sources to authenticated;

create or replace function private.prevent_live_exam_workflow_change()
returns trigger
language plpgsql
security definer
set search_path = ''
as $function$
begin
  if (old.delivery_type, old.quiz_result_policy, old.supervision_mode)
       is distinct from
     (new.delivery_type, new.quiz_result_policy, new.supervision_mode)
     and (
       old.status = 'Published'
       or exists (
         select 1 from public.exam_sessions s
         where s.exam_id = old.id and s.organization_id = old.organization_id)
       or exists (
         select 1
         from public.quiz_attempts a
         join public.exam_sessions s on s.id = a.session_id
         where s.exam_id = old.id and a.organization_id = old.organization_id)
     )
  then
    raise exception 'EXAM_WORKFLOW_IMMUTABLE' using errcode = '55000';
  end if;
  if new.delivery_type = 'FileSubmission' then
    new.quiz_result_policy := 'Hidden';
  end if;
  return new;
end
$function$;
drop trigger if exists trg_prevent_live_exam_workflow_change on public.exams;
create trigger trg_prevent_live_exam_workflow_change
before update of delivery_type, quiz_result_policy, supervision_mode on public.exams
for each row execute function private.prevent_live_exam_workflow_change();

-- Direct student reads must never include score. Teachers use the explicit RPC
-- below; service_role keeps its bypass privileges for projection/outbox work.
drop policy if exists quiz_attempts_student_own on public.quiz_attempts;
create policy quiz_attempts_student_own on public.quiz_attempts for select to authenticated
  using (
    organization_id = (select public.current_organization_id())
    and exists (
      select 1 from public.session_participants p
      where p.id = participant_id and p.user_id = (select auth.uid())));
revoke select on public.quiz_attempts from authenticated;
grant select (
  id, organization_id, session_id, participant_id, exam_version, result_policy,
  status, started_at, deadline_at, finalized_at, max_score, snapshot_json,
  source_mode, cloud_version, created_at, updated_at)
on public.quiz_attempts to authenticated;

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
    'scoreVisible', a.status = 'Finalized' and a.result_policy = 'ShowAfterSubmission',
    'score', case
      when a.status = 'Finalized' and a.result_policy = 'ShowAfterSubmission' then a.score
      else null
    end,
    'maxScore', a.max_score,
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

create or replace function public.get_teacher_quiz_attempts(p_session_id uuid)
returns table (
  id uuid,
  participant_id uuid,
  status text,
  exam_version integer,
  result_policy text,
  started_at timestamptz,
  deadline_at timestamptz,
  finalized_at timestamptz,
  score numeric,
  max_score numeric)
language plpgsql
stable
security definer
set search_path = ''
as $function$
declare
  v_session public.exam_sessions%rowtype := private.require_public_session_teacher(p_session_id);
begin
  return query
  select a.id, a.participant_id, a.status, a.exam_version, a.result_policy,
         a.started_at, a.deadline_at, a.finalized_at, a.score, a.max_score
  from public.quiz_attempts a
  where a.session_id = v_session.id
    and a.organization_id = v_session.organization_id
  order by a.started_at desc, a.id desc;
end
$function$;
revoke all on function public.get_teacher_quiz_attempts(uuid) from public, anon;
grant execute on function public.get_teacher_quiz_attempts(uuid) to authenticated;

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
  where q.exam_id = v_session.exam_id and q.version = v_session.exam_version
    and q.organization_id = v_profile.organization_id;
  if v_max_score <= 0 then raise exception 'QUIZ_HAS_NO_QUESTIONS' using errcode = '55000'; end if;
  v_attempt_id := gen_random_uuid();
  insert into public.quiz_attempts(
    id, organization_id, session_id, participant_id, exam_version, result_policy,
    status, started_at, deadline_at, max_score, snapshot_json,
    finalize_idempotency_key, source_mode, cloud_version, created_at, updated_at)
  values (
    v_attempt_id, v_profile.organization_id, p_session_id, v_participant.id,
    v_session.exam_version, v_session.quiz_result_policy, 'InProgress',
    now(), v_deadline, v_max_score, v_snapshot, null, 'PublicCloud',
    private.next_public_cloud_version(), now(), now());
  return v_attempt_id;
end
$function$;
revoke all on function public.start_public_quiz_attempt(uuid,text) from public, anon;
grant execute on function public.start_public_quiz_attempt(uuid,text) to authenticated;

drop function if exists public.finalize_public_quiz_attempt(uuid,text);
create function public.finalize_public_quiz_attempt(
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
    select q.id, q.points,
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
  set status = 'Finalized', finalized_at = now(), score = v_score,
      finalize_idempotency_key = btrim(p_idempotency_key),
      cloud_version = private.next_public_cloud_version(), updated_at = now()
  where id = p_attempt_id;
  return public.get_public_quiz_attempt(p_attempt_id);
end
$function$;
revoke all on function public.finalize_public_quiz_attempt(uuid,text) from public, anon;
grant execute on function public.finalize_public_quiz_attempt(uuid,text) to authenticated;

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
    'sessionStatus', s.status,
    'examId', s.exam_id,
    'examVersion', s.exam_version,
    'deliveryType', s.delivery_type,
    'supervisionMode', s.supervision_mode,
    'resultPolicy', s.quiz_result_policy,
    'startedAtUtc', s.started_at,
    'durationMinutes', e.duration_minutes,
    'extraTimeMinutes', p.extra_time_minutes,
    'effectiveDeadlineUtc', case when s.started_at is null then null
      else s.started_at + make_interval(mins => e.duration_minutes + greatest(p.extra_time_minutes, 0)) end,
    'attemptId', a.id,
    'attemptStatus', a.status,
    'attemptDeadlineUtc', a.deadline_at,
    'scoreVisible', a.status = 'Finalized' and a.result_policy = 'ShowAfterSubmission',
    'score', case when a.status = 'Finalized' and a.result_policy = 'ShowAfterSubmission' then a.score else null end,
    'maxScore', case when a.status = 'Finalized' and a.result_policy = 'ShowAfterSubmission' then a.max_score else null end,
    'serverNowUtc', clock_timestamp(),
    'revision', greatest(p.cloud_version, coalesce(a.cloud_version, p.cloud_version)),
    'updatedAtUtc', greatest(p.updated_at, coalesce(a.updated_at, p.updated_at)))
  into v_result
  from public.exam_sessions s
  join public.exams e on e.id = s.exam_id and e.organization_id = s.organization_id
  join public.session_participants p
    on p.session_id = s.id and p.organization_id = s.organization_id
   and p.user_id = v_profile.id and p.source_mode = 'PublicCloud'
  left join public.quiz_attempts a
    on a.session_id = s.id and a.participant_id = p.id
   and a.organization_id = s.organization_id and a.source_mode = 'PublicCloud'
  where s.id = p_session_id
    and s.organization_id = v_profile.organization_id
    and s.access_mode = 'PublicCloud'
    and exists (
      select 1 from public.class_members m
      where m.class_id = s.class_id and m.organization_id = s.organization_id
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
set schema_version = 17, updated_at = now()
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
      'finalize_public_quiz_attempt','get_public_quiz_attempt',
      'get_teacher_quiz_attempts','verify_public_submission_archive',
      'get_public_exam_file_download','approve_public_participant',
      'reject_public_participant','bulk_approve_public_participants',
      'add_public_participant_extra_time','allow_public_resubmission',
      'reject_public_submission','approve_public_enrollment_request',
      'reject_public_enrollment_request','get_public_student_timeline'),
    'buckets', coalesce((
      select jsonb_agg(id order by id)
      from storage.buckets
      where id in ('exam-archives','public-submission-archives')
    ), '[]'::jsonb));
end
$function$;
revoke all on function public.get_examtransfer_cloud_capabilities() from public, anon;
grant execute on function public.get_examtransfer_cloud_capabilities() to authenticated, service_role;

commit;
