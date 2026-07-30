begin;

alter table public.quiz_import_sources
  add column if not exists cloud_version bigint not null default 0;

update public.quiz_import_sources
set cloud_version = 0
where cloud_version is null;

update public.examtransfer_cloud_meta
set schema_version = 18,
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

revoke all on function public.get_examtransfer_cloud_capabilities()
  from public, anon;
grant execute on function public.get_examtransfer_cloud_capabilities()
  to authenticated, service_role;

commit;
