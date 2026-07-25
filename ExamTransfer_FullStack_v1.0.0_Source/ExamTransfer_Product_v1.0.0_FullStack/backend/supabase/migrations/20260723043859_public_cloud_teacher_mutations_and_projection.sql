begin;

-- This migration intentionally follows the already-published PublicCloud
-- migrations. It does not rewrite their history.
create schema if not exists private;
revoke all on schema private from public, anon, authenticated;

alter table public.class_enrollment_requests
  add column if not exists decision_reason text;

-- Reassert the compatibility constraints in case a project applied the first
-- PublicCloud migration before completion_v2. LAN history remains unrestricted.
drop index if exists public.ux_submission_files_submission;
drop index if exists public.ux_public_submission_idempotency;
create unique index if not exists ux_public_submission_single_file
  on public.submission_files(submission_id)
  where source_mode = 'PublicCloud';
create unique index if not exists ux_public_submission_idempotency
  on public.submissions(participant_id, idempotency_key)
  where source_mode = 'PublicCloud' and idempotency_key is not null;

create or replace function public.enforce_student_submission_policy()
returns trigger
language plpgsql
set search_path = ''
as $function$
begin
  if new.source_mode <> 'PublicCloud' then return new; end if;
  if new.size_bytes <= 0 or new.size_bytes > 10485760 then
    raise exception 'SUBMISSION_TOO_LARGE' using errcode = '22023';
  end if;
  if lower(new.name) !~ '\.(zip|rar|7z)$' then
    raise exception 'SUBMISSION_ARCHIVE_REQUIRED' using errcode = '22023';
  end if;
  if exists (
    select 1 from public.submission_files f
    where f.submission_id = new.submission_id
      and f.source_mode = 'PublicCloud'
      and f.id <> new.id
  ) then
    raise exception 'SUBMISSION_FILE_COUNT_INVALID' using errcode = '23505';
  end if;
  return new;
end
$function$;
revoke all on function public.enforce_student_submission_policy() from public, anon, authenticated;

create table if not exists private.public_teacher_mutation_requests (
  request_id uuid primary key,
  organization_id uuid not null,
  actor_id uuid not null,
  action text not null,
  entity_scope text not null,
  result_json jsonb,
  created_at timestamptz not null default now(),
  completed_at timestamptz
);
revoke all on table private.public_teacher_mutation_requests from public, anon, authenticated, service_role;

create or replace function private.require_public_session_teacher(p_session_id uuid)
returns public.exam_sessions
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_actor public.profiles%rowtype;
  v_session public.exam_sessions%rowtype;
  v_exam public.exams%rowtype;
begin
  if (select auth.uid()) is null then
    raise exception 'AUTHENTICATION_REQUIRED' using errcode = '28000';
  end if;
  select * into v_actor from public.profiles
  where id = (select auth.uid()) and is_active = true;
  if not found or v_actor.role not in ('Teacher','Admin') then
    raise exception 'TEACHER_ROLE_REQUIRED' using errcode = '42501';
  end if;
  select * into v_session from public.exam_sessions where id = p_session_id;
  if not found or v_session.access_mode <> 'PublicCloud' then
    raise exception 'PUBLIC_SESSION_NOT_FOUND' using errcode = 'P0002';
  end if;
  select * into v_exam from public.exams
  where id = v_session.exam_id and organization_id = v_session.organization_id;
  if not found
     or v_actor.organization_id <> v_session.organization_id
     or (v_actor.role <> 'Admin' and v_exam.created_by <> v_actor.id) then
    raise exception 'PUBLIC_SESSION_FORBIDDEN' using errcode = '42501';
  end if;
  return v_session;
end
$function$;
revoke all on function private.require_public_session_teacher(uuid) from public, anon, authenticated, service_role;

create or replace function private.require_public_class_teacher(p_class_id uuid)
returns public.classes
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_actor public.profiles%rowtype;
  v_class public.classes%rowtype;
begin
  if (select auth.uid()) is null then
    raise exception 'AUTHENTICATION_REQUIRED' using errcode = '28000';
  end if;
  select * into v_actor from public.profiles
  where id = (select auth.uid()) and is_active = true;
  if not found or v_actor.role not in ('Teacher','Admin') then
    raise exception 'TEACHER_ROLE_REQUIRED' using errcode = '42501';
  end if;
  select * into v_class from public.classes
  where id = p_class_id and access_mode = 'Public';
  if not found
     or v_actor.organization_id <> v_class.organization_id
     or (v_actor.role <> 'Admin' and v_class.created_by <> v_actor.id) then
    raise exception 'PUBLIC_CLASS_FORBIDDEN' using errcode = '42501';
  end if;
  return v_class;
end
$function$;
revoke all on function private.require_public_class_teacher(uuid) from public, anon, authenticated, service_role;

create or replace function private.assert_public_participant_organization(
  p_participant public.session_participants, p_organization_id uuid)
returns void
language plpgsql
security definer
set search_path = ''
as $function$
begin
  if p_participant.organization_id <> p_organization_id
     or p_participant.user_id is null
     or not exists (
       select 1 from public.profiles p
       where p.id = p_participant.user_id
         and p.organization_id = p_organization_id
         and p.is_active = true
     ) then
    raise exception 'PUBLIC_PARTICIPANT_ORGANIZATION_MISMATCH' using errcode = '42501';
  end if;
end
$function$;
revoke all on function private.assert_public_participant_organization(public.session_participants,uuid)
  from public, anon, authenticated, service_role;

create or replace function private.begin_public_teacher_mutation(
  p_request_id uuid, p_organization_id uuid, p_action text, p_entity_scope text)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare v_existing private.public_teacher_mutation_requests%rowtype;
begin
  if p_request_id is null then
    raise exception 'REQUEST_ID_REQUIRED' using errcode = '22023';
  end if;
  insert into private.public_teacher_mutation_requests(
    request_id, organization_id, actor_id, action, entity_scope)
  values (p_request_id, p_organization_id, (select auth.uid()), p_action, p_entity_scope)
  on conflict (request_id) do nothing;
  if not found then
    select * into v_existing
    from private.public_teacher_mutation_requests
    where request_id = p_request_id;
    if v_existing.organization_id <> p_organization_id
       or v_existing.actor_id <> (select auth.uid())
       or v_existing.action <> p_action
       or v_existing.entity_scope <> p_entity_scope then
      raise exception 'REQUEST_ID_REUSE_MISMATCH' using errcode = '22023';
    end if;
    if v_existing.result_json is null then
      raise exception 'REQUEST_ALREADY_IN_PROGRESS' using errcode = '55000';
    end if;
    return v_existing.result_json;
  end if;
  return null;
end
$function$;
revoke all on function private.begin_public_teacher_mutation(uuid,uuid,text,text) from public, anon, authenticated, service_role;

create or replace function private.finish_public_teacher_mutation(p_request_id uuid, p_result jsonb)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
begin
  update private.public_teacher_mutation_requests
  set result_json = p_result, completed_at = now()
  where request_id = p_request_id and actor_id = (select auth.uid());
  if not found then raise exception 'MUTATION_REQUEST_NOT_FOUND' using errcode = 'P0002'; end if;
  return p_result;
end
$function$;
revoke all on function private.finish_public_teacher_mutation(uuid,jsonb) from public, anon, authenticated, service_role;

create or replace function private.public_participant_result(p_participant_id uuid)
returns jsonb
language sql
stable
security definer
set search_path = ''
as $function$
  select jsonb_build_object(
    'participantId', p.id,
    'sessionId', p.session_id,
    'status', p.status,
    'approvedAt', p.approved_at,
    'extraTimeMinutes', p.extra_time_minutes,
    'resubmitAllowed', p.resubmit_allowed,
    'resubmitReason', p.resubmit_reason,
    'cloudVersion', p.cloud_version,
    'updatedAt', p.updated_at,
    'effectiveDeadline', case when s.started_at is null then null else
      s.started_at + make_interval(mins => e.duration_minutes + greatest(p.extra_time_minutes, 0)) end)
  from public.session_participants p
  join public.exam_sessions s on s.id = p.session_id
  join public.exams e on e.id = s.exam_id
  where p.id = p_participant_id
$function$;
revoke all on function private.public_participant_result(uuid) from public, anon, authenticated, service_role;

create or replace function private.write_public_teacher_audit(
  p_organization_id uuid, p_session_id uuid, p_action text,
  p_entity_type text, p_entity_id uuid, p_request_id uuid,
  p_before jsonb, p_after jsonb)
returns void
language sql
security definer
set search_path = ''
as $function$
  insert into public.audit_logs(
    id, organization_id, session_id, actor_id, action, entity_type,
    entity_id, trace_id, before_json, after_json, created_at, updated_at)
  values (
    gen_random_uuid(), p_organization_id, p_session_id, (select auth.uid())::text,
    p_action, p_entity_type, p_entity_id::text, p_request_id::text,
    p_before, p_after, now(), now())
$function$;
revoke all on function private.write_public_teacher_audit(uuid,uuid,text,text,uuid,uuid,jsonb,jsonb)
  from public, anon, authenticated, service_role;

create or replace function public.approve_public_participant(
  p_session_id uuid, p_participant_id uuid, p_request_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_session public.exam_sessions%rowtype := private.require_public_session_teacher(p_session_id);
  v_before public.session_participants%rowtype;
  v_cached jsonb;
  v_result jsonb;
begin
  v_cached := private.begin_public_teacher_mutation(
    p_request_id, v_session.organization_id, 'ApprovePublicParticipant',
    p_session_id::text || ':' || p_participant_id::text);
  if v_cached is not null then return v_cached; end if;
  select * into v_before from public.session_participants
  where id = p_participant_id and session_id = p_session_id
    and organization_id = v_session.organization_id and source_mode = 'PublicCloud'
  for update;
  if not found then raise exception 'PUBLIC_PARTICIPANT_NOT_FOUND' using errcode = 'P0002'; end if;
  perform private.assert_public_participant_organization(v_before, v_session.organization_id);
  if v_before.status not in ('PendingApproval','Approved') then
    raise exception 'PARTICIPANT_NOT_PENDING' using errcode = '55000';
  end if;
  if v_before.status = 'PendingApproval' then
    update public.session_participants
    set status = 'Approved', approved_at = now(), updated_at = now()
    where id = p_participant_id;
  end if;
  v_result := private.public_participant_result(p_participant_id);
  perform private.write_public_teacher_audit(
    v_session.organization_id, p_session_id, 'ApprovePublicParticipant',
    'session_participants', p_participant_id, p_request_id,
    to_jsonb(v_before), v_result);
  return private.finish_public_teacher_mutation(p_request_id, v_result);
end
$function$;

create or replace function public.reject_public_participant(
  p_session_id uuid, p_participant_id uuid, p_reason text, p_request_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_session public.exam_sessions%rowtype := private.require_public_session_teacher(p_session_id);
  v_before public.session_participants%rowtype;
  v_cached jsonb;
  v_result jsonb;
begin
  v_cached := private.begin_public_teacher_mutation(
    p_request_id, v_session.organization_id, 'RejectPublicParticipant',
    p_session_id::text || ':' || p_participant_id::text);
  if v_cached is not null then return v_cached; end if;
  select * into v_before from public.session_participants
  where id = p_participant_id and session_id = p_session_id
    and organization_id = v_session.organization_id and source_mode = 'PublicCloud'
  for update;
  if not found then raise exception 'PUBLIC_PARTICIPANT_NOT_FOUND' using errcode = 'P0002'; end if;
  perform private.assert_public_participant_organization(v_before, v_session.organization_id);
  if v_before.status <> 'Rejected' and (
      coalesce(v_before.submission_status, 'NotStarted') not in ('NotStarted','Failed')
      or exists (select 1 from public.submissions s where s.participant_id = p_participant_id)
  ) then raise exception 'PARTICIPANT_ALREADY_STARTED' using errcode = '55000'; end if;
  if v_before.status <> 'Rejected' then
    update public.session_participants
    set status = 'Rejected', approved_at = null, updated_at = now()
    where id = p_participant_id;
  end if;
  v_result := private.public_participant_result(p_participant_id);
  perform private.write_public_teacher_audit(
    v_session.organization_id, p_session_id, 'RejectPublicParticipant',
    'session_participants', p_participant_id, p_request_id,
    to_jsonb(v_before), v_result || jsonb_build_object('reason', nullif(btrim(coalesce(p_reason,'')),'')));
  return private.finish_public_teacher_mutation(p_request_id, v_result);
end
$function$;

create or replace function public.bulk_approve_public_participants(
  p_session_id uuid, p_participant_ids uuid[], p_request_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_session public.exam_sessions%rowtype := private.require_public_session_teacher(p_session_id);
  v_cached jsonb;
  v_result jsonb;
  v_total integer;
  v_approved integer;
begin
  v_total := coalesce(cardinality(p_participant_ids), 0);
  if v_total < 1 or v_total > 200
     or (select count(distinct x) from unnest(p_participant_ids) x) <> v_total then
    raise exception 'PARTICIPANT_LIST_INVALID' using errcode = '22023';
  end if;
  v_cached := private.begin_public_teacher_mutation(
    p_request_id, v_session.organization_id, 'BulkApprovePublicParticipants',
    p_session_id::text || ':' || array_to_string(p_participant_ids, ','));
  if v_cached is not null then return v_cached; end if;
  perform 1 from public.session_participants
  where id = any(p_participant_ids) for update;
  if (select count(*) from public.session_participants p
      where p.id = any(p_participant_ids)
        and p.session_id = p_session_id
        and p.organization_id = v_session.organization_id
        and p.source_mode = 'PublicCloud'
        and p.status in ('PendingApproval','Approved')
        and p.user_id is not null
        and exists (
          select 1 from public.profiles profile
          where profile.id = p.user_id
            and profile.organization_id = v_session.organization_id
            and profile.is_active = true
        )) <> v_total then
    raise exception 'BULK_PARTICIPANT_SCOPE_INVALID' using errcode = '55000';
  end if;
  update public.session_participants
  set status = 'Approved', approved_at = now(), updated_at = now()
  where id = any(p_participant_ids) and status = 'PendingApproval';
  get diagnostics v_approved = row_count;
  select jsonb_build_object(
    'approvedCount', v_approved,
    'skippedCount', v_total - v_approved,
    'participants', jsonb_agg(private.public_participant_result(x) order by x))
  into v_result from unnest(p_participant_ids) x;
  perform private.write_public_teacher_audit(
    v_session.organization_id, p_session_id, 'BulkApprovePublicParticipants',
    'session_participants', p_session_id, p_request_id, null, v_result);
  return private.finish_public_teacher_mutation(p_request_id, v_result);
end
$function$;

create or replace function public.add_public_participant_extra_time(
  p_session_id uuid, p_participant_id uuid, p_minutes integer, p_reason text, p_request_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_session public.exam_sessions%rowtype := private.require_public_session_teacher(p_session_id);
  v_before public.session_participants%rowtype;
  v_cached jsonb;
  v_result jsonb;
begin
  if p_minutes < 1 or p_minutes > 480 or length(btrim(coalesce(p_reason,''))) < 3 then
    raise exception 'EXTRA_TIME_INPUT_INVALID' using errcode = '22023';
  end if;
  if v_session.status not in ('InProgress','Paused','Collecting') then
    raise exception 'SESSION_NOT_ACTIVE' using errcode = '55000';
  end if;
  v_cached := private.begin_public_teacher_mutation(
    p_request_id, v_session.organization_id, 'AddPublicParticipantExtraTime',
    p_session_id::text || ':' || p_participant_id::text || ':' || p_minutes::text);
  if v_cached is not null then return v_cached; end if;
  select * into v_before from public.session_participants
  where id = p_participant_id and session_id = p_session_id
    and organization_id = v_session.organization_id and source_mode = 'PublicCloud'
  for update;
  if not found or v_before.status not in ('Approved','Disconnected') then
    raise exception 'PUBLIC_PARTICIPANT_NOT_ACTIVE' using errcode = 'P0002';
  end if;
  perform private.assert_public_participant_organization(v_before, v_session.organization_id);
  if v_before.extra_time_minutes + p_minutes > 480 then
    raise exception 'EXTRA_TIME_LIMIT_EXCEEDED' using errcode = '22023';
  end if;
  update public.session_participants
  set extra_time_minutes = extra_time_minutes + p_minutes, updated_at = now()
  where id = p_participant_id;
  v_result := private.public_participant_result(p_participant_id);
  perform private.write_public_teacher_audit(
    v_session.organization_id, p_session_id, 'AddPublicParticipantExtraTime',
    'session_participants', p_participant_id, p_request_id,
    to_jsonb(v_before), v_result || jsonb_build_object('reason', btrim(p_reason)));
  return private.finish_public_teacher_mutation(p_request_id, v_result);
end
$function$;

create or replace function public.allow_public_resubmission(
  p_participant_id uuid, p_reason text, p_request_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_participant public.session_participants%rowtype;
  v_session public.exam_sessions%rowtype;
  v_cached jsonb;
  v_result jsonb;
begin
  if length(btrim(coalesce(p_reason,''))) < 3 then
    raise exception 'RESUBMISSION_REASON_REQUIRED' using errcode = '22023';
  end if;
  select * into v_participant from public.session_participants
  where id = p_participant_id and source_mode = 'PublicCloud' for update;
  if not found then raise exception 'PUBLIC_PARTICIPANT_NOT_FOUND' using errcode = 'P0002'; end if;
  v_session := private.require_public_session_teacher(v_participant.session_id);
  perform private.assert_public_participant_organization(v_participant, v_session.organization_id);
  v_cached := private.begin_public_teacher_mutation(
    p_request_id, v_session.organization_id, 'AllowPublicResubmission',
    p_participant_id::text);
  if v_cached is not null then return v_cached; end if;
  if v_participant.submission_status not in ('Submitted','LateSubmitted','Rejected') then
    raise exception 'RESUBMISSION_NOT_APPLICABLE' using errcode = '55000';
  end if;
  update public.session_participants
  set resubmit_allowed = true, resubmit_reason = btrim(p_reason), updated_at = now()
  where id = p_participant_id;
  v_result := private.public_participant_result(p_participant_id);
  perform private.write_public_teacher_audit(
    v_session.organization_id, v_session.id, 'AllowPublicResubmission',
    'session_participants', p_participant_id, p_request_id,
    to_jsonb(v_participant), v_result);
  return private.finish_public_teacher_mutation(p_request_id, v_result);
end
$function$;

create or replace function public.reject_public_submission(
  p_submission_id uuid, p_reason text, p_request_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_submission public.submissions%rowtype;
  v_session public.exam_sessions%rowtype;
  v_cached jsonb;
  v_result jsonb;
begin
  if length(btrim(coalesce(p_reason,''))) < 3 then
    raise exception 'SUBMISSION_REJECTION_REASON_REQUIRED' using errcode = '22023';
  end if;
  select * into v_submission from public.submissions
  where id = p_submission_id and source_mode = 'PublicCloud' for update;
  if not found then raise exception 'PUBLIC_SUBMISSION_NOT_FOUND' using errcode = 'P0002'; end if;
  v_session := private.require_public_session_teacher(v_submission.session_id);
  if v_submission.organization_id <> v_session.organization_id
     or not exists (
       select 1 from public.session_participants p
       where p.id = v_submission.participant_id
         and p.session_id = v_session.id
         and p.organization_id = v_session.organization_id
         and p.source_mode = 'PublicCloud'
     ) then
    raise exception 'PUBLIC_SUBMISSION_ORGANIZATION_MISMATCH' using errcode = '42501';
  end if;
  v_cached := private.begin_public_teacher_mutation(
    p_request_id, v_session.organization_id, 'RejectPublicSubmission',
    p_submission_id::text);
  if v_cached is not null then return v_cached; end if;
  if v_submission.status not in ('Submitted','LateSubmitted','Rejected') then
    raise exception 'SUBMISSION_NOT_REJECTABLE' using errcode = '55000';
  end if;
  if v_submission.status <> 'Rejected'
     or v_submission.teacher_reject_reason is distinct from btrim(p_reason) then
    update public.submissions
    set status = 'Rejected', teacher_reject_reason = btrim(p_reason), updated_at = now()
    where id = p_submission_id;
    update public.session_participants
    set submission_status = 'Rejected', updated_at = now()
    where id = v_submission.participant_id and source_mode = 'PublicCloud';
  end if;
  select jsonb_build_object(
    'submissionId', s.id, 'sessionId', s.session_id,
    'participantId', s.participant_id, 'status', s.status,
    'teacherRejectReason', s.teacher_reject_reason,
    'cloudVersion', s.cloud_version, 'updatedAt', s.updated_at)
  into v_result from public.submissions s where s.id = p_submission_id;
  perform private.write_public_teacher_audit(
    v_session.organization_id, v_session.id, 'RejectPublicSubmission',
    'submissions', p_submission_id, p_request_id, to_jsonb(v_submission), v_result);
  return private.finish_public_teacher_mutation(p_request_id, v_result);
end
$function$;

create or replace function public.approve_public_enrollment_request(
  p_enrollment_request_id uuid, p_request_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_enrollment public.class_enrollment_requests%rowtype;
  v_class public.classes%rowtype;
  v_cached jsonb;
  v_result jsonb;
begin
  select * into v_enrollment from public.class_enrollment_requests
  where id = p_enrollment_request_id for update;
  if not found then raise exception 'ENROLLMENT_REQUEST_NOT_FOUND' using errcode = 'P0002'; end if;
  v_class := private.require_public_class_teacher(v_enrollment.class_id);
  if v_enrollment.organization_id <> v_class.organization_id
     or not exists (
       select 1 from public.profiles p
       where p.id = v_enrollment.student_user_id
         and p.organization_id = v_class.organization_id
         and p.is_active = true
     ) then
    raise exception 'ENROLLMENT_ORGANIZATION_MISMATCH' using errcode = '42501';
  end if;
  v_cached := private.begin_public_teacher_mutation(
    p_request_id, v_class.organization_id, 'ApprovePublicEnrollment',
    p_enrollment_request_id::text);
  if v_cached is not null then return v_cached; end if;
  if v_enrollment.status not in ('Pending','Approved') then
    raise exception 'ENROLLMENT_NOT_PENDING' using errcode = '55000';
  end if;
  if v_enrollment.status = 'Pending' then
    update public.class_enrollment_requests
    set status = 'Approved', decided_at = now(), decided_by = (select auth.uid()),
        decision_reason = null, updated_at = now()
    where id = p_enrollment_request_id;
  end if;
  select jsonb_build_object(
    'enrollmentRequestId', r.id, 'classId', r.class_id, 'status', r.status,
    'decidedAt', r.decided_at, 'cloudVersion', r.cloud_version, 'updatedAt', r.updated_at)
  into v_result from public.class_enrollment_requests r where r.id = p_enrollment_request_id;
  perform private.write_public_teacher_audit(
    v_class.organization_id, null, 'ApprovePublicEnrollment',
    'class_enrollment_requests', p_enrollment_request_id, p_request_id,
    to_jsonb(v_enrollment), v_result);
  return private.finish_public_teacher_mutation(p_request_id, v_result);
end
$function$;

create or replace function public.reject_public_enrollment_request(
  p_enrollment_request_id uuid, p_reason text, p_request_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_enrollment public.class_enrollment_requests%rowtype;
  v_class public.classes%rowtype;
  v_cached jsonb;
  v_result jsonb;
begin
  select * into v_enrollment from public.class_enrollment_requests
  where id = p_enrollment_request_id for update;
  if not found then raise exception 'ENROLLMENT_REQUEST_NOT_FOUND' using errcode = 'P0002'; end if;
  v_class := private.require_public_class_teacher(v_enrollment.class_id);
  if v_enrollment.organization_id <> v_class.organization_id
     or not exists (
       select 1 from public.profiles p
       where p.id = v_enrollment.student_user_id
         and p.organization_id = v_class.organization_id
         and p.is_active = true
     ) then
    raise exception 'ENROLLMENT_ORGANIZATION_MISMATCH' using errcode = '42501';
  end if;
  v_cached := private.begin_public_teacher_mutation(
    p_request_id, v_class.organization_id, 'RejectPublicEnrollment',
    p_enrollment_request_id::text);
  if v_cached is not null then return v_cached; end if;
  if v_enrollment.status not in ('Pending','Rejected') then
    raise exception 'ENROLLMENT_NOT_PENDING' using errcode = '55000';
  end if;
  if v_enrollment.status = 'Pending' then
    update public.class_enrollment_requests
    set status = 'Rejected', decided_at = now(), decided_by = (select auth.uid()),
        decision_reason = nullif(btrim(coalesce(p_reason,'')),''), updated_at = now()
    where id = p_enrollment_request_id;
  end if;
  select jsonb_build_object(
    'enrollmentRequestId', r.id, 'classId', r.class_id, 'status', r.status,
    'decidedAt', r.decided_at, 'cloudVersion', r.cloud_version, 'updatedAt', r.updated_at)
  into v_result from public.class_enrollment_requests r where r.id = p_enrollment_request_id;
  perform private.write_public_teacher_audit(
    v_class.organization_id, null, 'RejectPublicEnrollment',
    'class_enrollment_requests', p_enrollment_request_id, p_request_id,
    to_jsonb(v_enrollment), v_result);
  return private.finish_public_teacher_mutation(p_request_id, v_result);
end
$function$;

revoke all on function public.approve_public_participant(uuid,uuid,uuid) from public, anon;
revoke all on function public.reject_public_participant(uuid,uuid,text,uuid) from public, anon;
revoke all on function public.bulk_approve_public_participants(uuid,uuid[],uuid) from public, anon;
revoke all on function public.add_public_participant_extra_time(uuid,uuid,integer,text,uuid) from public, anon;
revoke all on function public.allow_public_resubmission(uuid,text,uuid) from public, anon;
revoke all on function public.reject_public_submission(uuid,text,uuid) from public, anon;
revoke all on function public.approve_public_enrollment_request(uuid,uuid) from public, anon;
revoke all on function public.reject_public_enrollment_request(uuid,text,uuid) from public, anon;
grant execute on function public.approve_public_participant(uuid,uuid,uuid) to authenticated;
grant execute on function public.reject_public_participant(uuid,uuid,text,uuid) to authenticated;
grant execute on function public.bulk_approve_public_participants(uuid,uuid[],uuid) to authenticated;
grant execute on function public.add_public_participant_extra_time(uuid,uuid,integer,text,uuid) to authenticated;
grant execute on function public.allow_public_resubmission(uuid,text,uuid) to authenticated;
grant execute on function public.reject_public_submission(uuid,text,uuid) to authenticated;
grant execute on function public.approve_public_enrollment_request(uuid,uuid) to authenticated;
grant execute on function public.reject_public_enrollment_request(uuid,text,uuid) to authenticated;

update public.examtransfer_cloud_meta set schema_version = 15, updated_at = now() where id = 1;

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
      'reject_public_enrollment_request'),
    'buckets', coalesce((
      select jsonb_agg(id order by id) from storage.buckets
      where id in ('exam-archives','public-submission-archives')
    ), '[]'::jsonb)
  );
end
$function$;
revoke all on function public.get_examtransfer_cloud_capabilities() from public, anon;
grant execute on function public.get_examtransfer_cloud_capabilities() to authenticated, service_role;

commit;
