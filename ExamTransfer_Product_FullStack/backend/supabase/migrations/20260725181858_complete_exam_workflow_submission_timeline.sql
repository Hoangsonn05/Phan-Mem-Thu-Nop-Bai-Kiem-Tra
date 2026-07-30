begin;

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
    'submissionStatus', p.submission_status,
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

commit;
