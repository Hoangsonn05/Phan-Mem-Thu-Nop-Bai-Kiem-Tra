begin;

create or replace function private.is_public_quiz_attempt_snapshot_payload_valid(
  p_snapshot jsonb)
returns boolean
language plpgsql
immutable
security invoker
set search_path = ''
as $function$
declare
  v_question jsonb;
  v_choice jsonb;
  v_question_id uuid;
  v_choice_id uuid;
  v_question_order integer;
  v_choice_order integer;
  v_points numeric;
  v_seen_question_ids uuid[] := '{}'::uuid[];
  v_seen_choice_ids uuid[] := '{}'::uuid[];
  v_seen_question_orders integer[] := '{}'::integer[];
  v_seen_choice_orders integer[];
begin
  if pg_catalog.jsonb_typeof(p_snapshot) <> 'array'
     or pg_catalog.jsonb_array_length(p_snapshot) not between 1 and 500 then
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
         or pg_catalog.length(pg_catalog.btrim(v_choice ->> 'choiceText')) not between 1 and 5000 then
        return false;
      end if;
      v_seen_choice_ids := pg_catalog.array_append(v_seen_choice_ids, v_choice_id);
      v_seen_choice_orders := pg_catalog.array_append(v_seen_choice_orders, v_choice_order);
    end loop;
  end loop;
  return true;
exception when others then
  return false;
end
$function$;

revoke all on function private.is_public_quiz_attempt_snapshot_payload_valid(jsonb)
  from public, anon, authenticated, service_role;

create or replace function public.save_public_quiz_answers(
  p_attempt_id uuid,
  p_question_id uuid,
  p_choice_ids jsonb,
  p_revision bigint,
  p_client_updated_at timestamptz)
returns bigint
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
  v_attempt public.quiz_attempts%rowtype;
  v_snapshot_question jsonb;
  v_existing_revision bigint;
  v_authoritative_revision bigint;
  v_count integer;
  v_distinct_count integer;
begin
  if p_revision <= 0
     or p_choice_ids is null
     or pg_catalog.jsonb_typeof(p_choice_ids) <> 'array' then
    raise exception 'QUIZ_ANSWER_INVALID' using errcode = '22023';
  end if;
  if pg_catalog.jsonb_array_length(p_choice_ids) > 100
     or pg_catalog.pg_column_size(p_choice_ids) > 16384 then
    raise exception 'QUIZ_ANSWER_TOO_LARGE' using errcode = '22023';
  end if;

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
    and s.status in ('InProgress','Paused')
    and (
      s.admission_mode = 'OpenRequest'
      or (
        s.admission_mode = 'ClassMembersOnly'
        and exists (
          select 1 from public.class_members m
          where m.class_id = s.class_id
            and m.user_id = p.user_id
            and m.organization_id = s.organization_id)))
  for update of a;
  if not found then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_NOT_FOUND' using errcode = 'P0002';
  end if;
  if v_attempt.status <> 'InProgress'
     or pg_catalog.now() > v_attempt.deadline_at then
    raise exception 'QUIZ_ATTEMPT_CLOSED' using errcode = '55000';
  end if;
  if not private.is_public_quiz_attempt_snapshot_payload_valid(v_attempt.snapshot_json) then
    raise exception 'QUIZ_ATTEMPT_SNAPSHOT_INVALID' using errcode = '55000';
  end if;

  select value into v_snapshot_question
  from pg_catalog.jsonb_array_elements(v_attempt.snapshot_json)
  where value ->> 'id' = p_question_id::text;
  if not found then
    raise exception 'QUIZ_QUESTION_NOT_FOUND' using errcode = 'P0002';
  end if;

  if exists (
    select 1
    from pg_catalog.jsonb_array_elements_text(p_choice_ids) x(value)
    where value !~* '^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$') then
    raise exception 'QUIZ_CHOICE_INVALID' using errcode = '22023';
  end if;
  select pg_catalog.count(*), pg_catalog.count(distinct value)
    into v_count, v_distinct_count
  from pg_catalog.jsonb_array_elements_text(p_choice_ids);
  if v_count <> v_distinct_count then
    raise exception 'QUIZ_CHOICE_DUPLICATE' using errcode = '22023';
  end if;
  if not (v_snapshot_question ->> 'multiple')::boolean and v_count > 1 then
    raise exception 'QUIZ_CHOICE_COUNT_INVALID' using errcode = '22023';
  end if;
  if exists (
    select 1
    from pg_catalog.jsonb_array_elements_text(p_choice_ids) selected(value)
    where not exists (
      select 1
      from pg_catalog.jsonb_array_elements(v_snapshot_question -> 'choices') choice
      where choice ->> 'id' = selected.value)) then
    raise exception 'QUIZ_CHOICE_INVALID' using errcode = '22023';
  end if;

  select revision into v_existing_revision
  from public.quiz_answers
  where attempt_id = p_attempt_id and question_id = p_question_id
  for update;
  if found and p_revision <= v_existing_revision then
    return v_existing_revision;
  end if;

  insert into public.quiz_answers(
    id, organization_id, attempt_id, question_id, choice_ids, revision,
    client_updated_at, source_mode, cloud_version, created_at, updated_at)
  values (
    gen_random_uuid(), v_profile.organization_id, p_attempt_id, p_question_id,
    p_choice_ids, p_revision,
    least(coalesce(p_client_updated_at, pg_catalog.now()), pg_catalog.now()),
    'PublicCloud', private.next_public_cloud_version(),
    pg_catalog.now(), pg_catalog.now())
  on conflict (attempt_id, question_id) do update
    set choice_ids = excluded.choice_ids,
        revision = excluded.revision,
        client_updated_at = excluded.client_updated_at,
        cloud_version = private.next_public_cloud_version(),
        updated_at = pg_catalog.now()
    where excluded.revision > public.quiz_answers.revision;

  select revision into v_authoritative_revision
  from public.quiz_answers
  where attempt_id = p_attempt_id and question_id = p_question_id;
  return v_authoritative_revision;
end
$function$;

revoke all on function public.save_public_quiz_answers(uuid,uuid,jsonb,bigint,timestamptz)
  from public, anon;
grant execute on function public.save_public_quiz_answers(uuid,uuid,jsonb,bigint,timestamptz)
  to authenticated;

update public.examtransfer_cloud_meta
set schema_version = 31,
    updated_at = pg_catalog.now()
where id = 1;

commit;
