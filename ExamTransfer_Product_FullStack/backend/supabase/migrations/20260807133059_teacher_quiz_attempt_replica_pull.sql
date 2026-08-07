begin;

create or replace function public.pull_teacher_quiz_attempts(
  p_organization_id uuid,
  p_cloud_version bigint,
  p_updated_at timestamptz,
  p_id uuid,
  p_limit integer)
returns table (
  id uuid,
  organization_id uuid,
  session_id uuid,
  participant_id uuid,
  exam_version integer,
  status text,
  started_at timestamptz,
  deadline_at timestamptz,
  finalized_at timestamptz,
  score numeric,
  max_score numeric,
  snapshot_json jsonb,
  finalize_idempotency_key text,
  created_at timestamptz,
  updated_at timestamptz,
  source_mode text,
  cloud_version bigint,
  result_policy text,
  auto_score numeric,
  grading_status text,
  general_comment text,
  grader_id uuid,
  graded_at timestamptz,
  returned_at timestamptz,
  attempt_number integer)
language plpgsql
stable
security definer
set search_path = ''
as $function$
declare
  v_actor public.profiles%rowtype;
begin
  if (select auth.uid()) is null then
    raise exception 'AUTHENTICATION_REQUIRED' using errcode = '28000';
  end if;

  select * into v_actor
  from public.profiles profile
  where profile.id = (select auth.uid())
    and profile.is_active = true;

  if not found or v_actor.role not in ('Admin','Teacher') then
    raise exception 'TEACHER_ROLE_REQUIRED' using errcode = '42501';
  end if;
  if p_organization_id is null
     or p_organization_id <> v_actor.organization_id then
    raise exception 'PUBLIC_ORGANIZATION_FORBIDDEN' using errcode = '42501';
  end if;
  if p_cloud_version is null or p_cloud_version < 0 then
    raise exception 'CLOUD_CURSOR_INVALID' using errcode = '22023';
  end if;
  if p_limit is null or p_limit < 1 or p_limit > 500 then
    raise exception 'PAGE_SIZE_INVALID' using errcode = '22023';
  end if;

  return query
  select
    attempt.id,
    attempt.organization_id,
    attempt.session_id,
    attempt.participant_id,
    attempt.exam_version,
    attempt.status,
    attempt.started_at,
    attempt.deadline_at,
    attempt.finalized_at,
    attempt.score,
    attempt.max_score,
    attempt.snapshot_json,
    attempt.finalize_idempotency_key,
    attempt.created_at,
    attempt.updated_at,
    attempt.source_mode,
    attempt.cloud_version,
    attempt.result_policy,
    attempt.auto_score,
    attempt.grading_status,
    attempt.general_comment,
    attempt.grader_id,
    attempt.graded_at,
    attempt.returned_at,
    attempt.attempt_number
  from public.quiz_attempts attempt
  where attempt.organization_id = v_actor.organization_id
    and attempt.source_mode = 'PublicCloud'
    and (
      attempt.cloud_version > p_cloud_version
      or (attempt.cloud_version = p_cloud_version and p_updated_at is null)
      or (attempt.cloud_version = p_cloud_version and attempt.updated_at > p_updated_at)
      or (attempt.cloud_version = p_cloud_version
          and attempt.updated_at = p_updated_at
          and (p_id is null or attempt.id > p_id)))
  order by attempt.cloud_version, attempt.updated_at, attempt.id
  limit p_limit;
end
$function$;

revoke all on function public.pull_teacher_quiz_attempts(
  uuid,bigint,timestamptz,uuid,integer)
  from public, anon, service_role;
grant execute on function public.pull_teacher_quiz_attempts(
  uuid,bigint,timestamptz,uuid,integer)
  to authenticated;

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
  return pg_catalog.jsonb_build_object(
    'schemaVersion',
      (select schema_version from public.examtransfer_cloud_meta where id = 1),
    'criticalRpcs', pg_catalog.jsonb_build_array(
      'join_public_session','join_open_public_session_by_room_code',
      'init_public_submission','finalize_public_submission',
      'upsert_public_device_heartbeat','ack_public_device_command',
      'report_public_violation',
      'start_public_quiz_attempt','save_public_quiz_answers',
      'finalize_public_quiz_attempt','get_public_quiz_attempt',
      'get_public_quiz_attempt_review','get_teacher_quiz_attempts',
      'pull_teacher_quiz_attempts',
      'save_public_quiz_grade','return_public_quiz_grade',
      'reopen_public_quiz_grade',
      'get_public_essay_grade','save_public_essay_grade',
      'return_public_essay_grade','reopen_public_essay_grade',
      'verify_public_submission_archive',
      'get_public_exam_manifest','get_public_exam_file_download',
      'approve_public_participant','reject_public_participant',
      'bulk_approve_public_participants','add_public_participant_extra_time',
      'allow_public_resubmission','reject_public_submission',
      'approve_public_enrollment_request','reject_public_enrollment_request',
      'get_public_student_timeline','send_public_teacher_message',
      'get_public_student_notification_events','get_student_results'),
    'buckets', coalesce((
      select pg_catalog.jsonb_agg(id order by id)
      from storage.buckets
      where id in ('exam-archives','public-submission-archives')
    ), '[]'::jsonb));
end
$function$;
revoke all on function public.get_examtransfer_cloud_capabilities()
  from public, anon;
grant execute on function public.get_examtransfer_cloud_capabilities()
  to authenticated, service_role;

update public.examtransfer_cloud_meta
set schema_version = 33,
    updated_at = pg_catalog.now()
where id = 1;

commit;
