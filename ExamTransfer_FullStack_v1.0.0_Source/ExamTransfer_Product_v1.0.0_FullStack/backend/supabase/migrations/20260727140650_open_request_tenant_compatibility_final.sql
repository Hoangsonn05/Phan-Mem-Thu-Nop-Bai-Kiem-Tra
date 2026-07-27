begin;

create or replace function private.enforce_public_tenant_consistency()
returns trigger
language plpgsql
security definer
set search_path = ''
as $function$
begin
  -- NEW is a polymorphic record. Branch on TG_TABLE_NAME before accessing
  -- table-specific fields so an update to one table never resolves fields
  -- that exist only on another table.
  if tg_table_name = 'exam_sessions' then
    if new.access_mode = 'PublicCloud' then
      if new.admission_mode = 'OpenRequest' then
        if new.class_id is not null then
          raise exception 'SESSION_OPENREQUEST_CLASS_FORBIDDEN'
            using errcode = '23514';
        end if;
        if not exists (
          select 1
          from public.exams e
          where e.id = new.exam_id
            and e.organization_id = new.organization_id
        ) then
          raise exception 'SESSION_TENANT_MISMATCH' using errcode = '23514';
        end if;
      elsif new.admission_mode = 'ClassMembersOnly' then
        if new.class_id is null then
          raise exception 'SESSION_CLASS_REQUIRED' using errcode = '23514';
        end if;
        if not exists (
          select 1
          from public.exams e
          join public.classes c
            on c.id = new.class_id
          where e.id = new.exam_id
            and e.organization_id = new.organization_id
            and c.organization_id = new.organization_id
            and e.class_id = new.class_id
        ) then
          raise exception 'SESSION_TENANT_MISMATCH' using errcode = '23514';
        end if;
      end if;
    end if;
  elsif tg_table_name = 'session_participants' then
    if new.source_mode = 'PublicCloud' and not exists (
      select 1 from public.exam_sessions s
      where s.id = new.session_id and s.organization_id = new.organization_id
        and s.access_mode = 'PublicCloud'
    ) then raise exception 'PARTICIPANT_TENANT_MISMATCH' using errcode = '23514'; end if;
  elsif tg_table_name = 'public_class_assignments' then
    if not exists (
      select 1 from public.classes c join public.exams e on e.id = new.exam_id
      where c.id = new.class_id and c.organization_id = new.organization_id
        and e.organization_id = new.organization_id and e.class_id = new.class_id
    ) then raise exception 'ASSIGNMENT_TENANT_MISMATCH' using errcode = '23514'; end if;
  elsif tg_table_name = 'public_device_connections' then
    if not exists (
      select 1 from public.exam_sessions s
      join public.session_participants p on p.session_id = s.id
      where s.id = new.session_id and p.id = new.participant_id
        and s.organization_id = new.organization_id
        and p.organization_id = new.organization_id
        and p.user_id = new.user_id and s.access_mode = 'PublicCloud'
    ) then raise exception 'DEVICE_TENANT_MISMATCH' using errcode = '23514'; end if;
  elsif tg_table_name = 'public_device_commands' then
    if not exists (
      select 1 from public.public_device_connections c
      where c.session_id = new.session_id and c.device_id = new.device_id
        and c.organization_id = new.organization_id
    ) then raise exception 'COMMAND_DEVICE_MISMATCH' using errcode = '23514'; end if;
  elsif tg_table_name = 'public_device_command_results' then
    if not exists (
      select 1 from public.public_device_commands c
      where c.command_id = new.command_id and c.device_id = new.device_id
        and c.organization_id = new.organization_id
    ) then raise exception 'COMMAND_RESULT_MISMATCH' using errcode = '23514'; end if;
  elsif tg_table_name = 'violations' then
    if new.source_mode = 'PublicCloud' and not exists (
      select 1 from public.session_participants p
      join public.exam_sessions s on s.id = p.session_id
      where p.id = new.participant_id and p.session_id = new.session_id
        and p.organization_id = new.organization_id
        and s.organization_id = new.organization_id
        and s.class_id is not distinct from new.class_id
        and s.access_mode = 'PublicCloud'
    ) then raise exception 'VIOLATION_TENANT_MISMATCH' using errcode = '23514'; end if;
  elsif tg_table_name = 'submissions' then
    if new.source_mode = 'PublicCloud' and not exists (
      select 1 from public.session_participants p
      join public.exam_sessions s on s.id = p.session_id
      where p.id = new.participant_id and p.session_id = new.session_id
        and p.organization_id = new.organization_id
        and s.organization_id = new.organization_id and s.access_mode = 'PublicCloud'
    ) then raise exception 'SUBMISSION_TENANT_MISMATCH' using errcode = '23514'; end if;
  elsif tg_table_name = 'submission_files' then
    if new.source_mode = 'PublicCloud' and not exists (
      select 1 from public.submissions s
      where s.id = new.submission_id and s.organization_id = new.organization_id
        and s.source_mode = 'PublicCloud'
    ) then raise exception 'SUBMISSION_FILE_TENANT_MISMATCH' using errcode = '23514'; end if;
  elsif tg_table_name = 'quiz_attempts' then
    if new.source_mode = 'PublicCloud' and not exists (
      select 1 from public.session_participants p
      join public.exam_sessions s on s.id = p.session_id
      where p.id = new.participant_id and p.session_id = new.session_id
        and p.organization_id = new.organization_id
        and s.organization_id = new.organization_id and s.access_mode = 'PublicCloud'
    ) then raise exception 'QUIZ_ATTEMPT_TENANT_MISMATCH' using errcode = '23514'; end if;
  elsif tg_table_name = 'quiz_answers' then
    if new.source_mode = 'PublicCloud' and not exists (
      select 1 from public.quiz_attempts a
      join public.quiz_questions q on q.id = new.question_id
      where a.id = new.attempt_id and a.organization_id = new.organization_id
        and q.organization_id = new.organization_id and a.source_mode = 'PublicCloud'
    ) then raise exception 'QUIZ_ANSWER_TENANT_MISMATCH' using errcode = '23514'; end if;
  end if;
  return new;
end
$function$;

revoke all on function private.enforce_public_tenant_consistency()
  from public, anon, authenticated, service_role;

update public.examtransfer_cloud_meta
set schema_version = 21,
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
