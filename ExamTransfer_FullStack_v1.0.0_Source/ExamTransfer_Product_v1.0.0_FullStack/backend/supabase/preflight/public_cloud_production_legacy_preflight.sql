-- Read-only preflight. Safe before any PublicCloud migration exists.
-- Run with ON_ERROR_STOP=1 and treat every BLOCKER line as a stop condition.
do $preflight$
declare
  v_count bigint;
  v_has_source_mode boolean;
  v_table text;
  v_exists boolean;
  v_history_available boolean := false;
  v_public_classes_applied boolean := false;
  v_completion_applied boolean := false;
  v_column_name text;
  v_columns text[] := array[
    'session_participants.source_mode',
    'session_participants.cloud_version',
    'submissions.source_mode',
    'submissions.cloud_version',
    'submission_files.source_mode',
    'submission_files.cloud_version'
  ];
  v_column text;
begin
  raise notice 'PASS|preflight|read-only catalog and aggregate checks started';

  v_history_available := to_regclass('supabase_migrations.schema_migrations') is not null;
  if v_history_available then
    execute $sql$
      select exists (
        select 1 from supabase_migrations.schema_migrations
        where version = '20260722141147')
    $sql$ into v_public_classes_applied;
    execute $sql$
      select exists (
        select 1 from supabase_migrations.schema_migrations
        where version = '20260722161450')
    $sql$ into v_completion_applied;
  end if;

  foreach v_table in array array[
    'organizations','profiles','classes','class_members','exams','exam_sessions',
    'session_participants','submissions','submission_files','violations',
    'class_enrollment_requests','public_device_connections',
    'public_device_commands','public_device_command_results',
    'quiz_attempts','quiz_answers'
  ] loop
    v_exists := to_regclass('public.' || v_table) is not null;
    if v_exists then
      execute format('select count(*) from public.%I', v_table) into v_count;
      raise notice 'PASS|table|public.% exists; rows=%', v_table, v_count;
    else
      if v_table in ('organizations','profiles','classes','exams','exam_sessions',
                     'session_participants','submissions','submission_files') then
        raise notice 'BLOCKER|table|public.% is missing', v_table;
      else
        raise notice 'WARNING|table|public.% is not installed yet', v_table;
      end if;
    end if;
  end loop;

  foreach v_column in array v_columns loop
    select exists (
      select 1
      from information_schema.columns c
      where c.table_schema = 'public'
        and c.table_name = split_part(v_column, '.', 1)
        and c.column_name = split_part(v_column, '.', 2)
    ) into v_exists;
    if v_exists then
      raise notice 'PASS|column|public.% exists', v_column;
    else
      raise notice 'WARNING|column|public.% is not installed yet', v_column;
    end if;
  end loop;

  if to_regclass('public.submission_files') is not null then
    select exists (
      select 1 from information_schema.columns
      where table_schema = 'public' and table_name = 'submission_files'
        and column_name = 'source_mode'
    ) into v_has_source_mode;
    if v_has_source_mode then
      execute $sql$
        select count(*) from (
          select submission_id from public.submission_files
          where source_mode = 'PublicCloud'
          group by submission_id having count(*) > 1
        ) d
      $sql$ into v_count;
      if v_count > 0 then
        raise notice 'BLOCKER|submission_files|% PublicCloud submissions have multiple files', v_count;
      else
        raise notice 'PASS|submission_files|no multi-file PublicCloud submission';
      end if;

      execute $sql$
        select count(*) from (
          select submission_id from public.submission_files
          where source_mode <> 'PublicCloud'
          group by submission_id having count(*) > 1
        ) d
      $sql$ into v_count;
      if v_count > 0 then
        raise notice 'WARNING|submission_files|% legacy LAN submissions have multiple files; partial index will not block them', v_count;
      else
        raise notice 'PASS|submission_files|no legacy LAN multi-file rows';
      end if;
    else
      execute $sql$
        select count(*) from (
          select submission_id from public.submission_files
          group by submission_id having count(*) > 1
        ) d
      $sql$ into v_count;
      if v_count > 0 then
        raise notice 'WARNING|submission_files|source_mode absent; % legacy multi-file groups exist. The checked-in migration uses a PublicCloud-only partial index and must remain unchanged before push', v_count;
      else
        raise notice 'PASS|submission_files|no legacy multi-file groups';
      end if;
    end if;
  end if;

  if to_regclass('public.submissions') is not null
     and exists (
       select 1 from information_schema.columns
       where table_schema = 'public' and table_name = 'submissions'
         and column_name = 'idempotency_key'
     ) then
    select exists (
      select 1 from information_schema.columns
      where table_schema = 'public' and table_name = 'submissions'
        and column_name = 'source_mode'
    ) into v_has_source_mode;
    if v_has_source_mode then
      execute $sql$
        select count(*) from (
          select participant_id, idempotency_key
          from public.submissions
          where source_mode = 'PublicCloud' and idempotency_key is not null
          group by participant_id, idempotency_key having count(*) > 1
        ) d
      $sql$ into v_count;
      if v_count > 0 then
        raise notice 'BLOCKER|idempotency|% duplicate PublicCloud keys', v_count;
      else
        raise notice 'PASS|idempotency|no duplicate PublicCloud keys';
      end if;
    else
      execute $sql$
        select count(*) from (
          select participant_id, idempotency_key
          from public.submissions
          where idempotency_key is not null
          group by participant_id, idempotency_key having count(*) > 1
        ) d
      $sql$ into v_count;
      raise notice 'WARNING|idempotency|source_mode absent; % duplicate groups cannot yet be classified', v_count;
    end if;
  else
    raise notice 'WARNING|idempotency|submissions.idempotency_key is not installed yet';
  end if;

  for v_table in
    select indexname
    from pg_indexes
    where schemaname = 'public'
      and tablename in ('submissions','submission_files')
    order by indexname
  loop
    raise notice 'PASS|index|%', v_table;
  end loop;
  if exists (
    select 1 from pg_indexes
    where schemaname = 'public' and indexname = 'ux_submission_files_submission'
  ) then
    if v_completion_applied then
      raise notice 'BLOCKER|index|global ux_submission_files_submission still exists although completion migration is recorded';
    else
      raise notice 'WARNING|index|global ux_submission_files_submission exists; completion migration must replace it without deleting data';
    end if;
  end if;

  if to_regclass('public.submissions') is not null
     and to_regclass('public.session_participants') is not null then
    execute $sql$
      select count(*)
      from public.submissions s
      left join public.session_participants p on p.id = s.participant_id
      where p.id is null
    $sql$ into v_count;
    if v_count > 0 then
      raise notice 'BLOCKER|orphan|% submissions reference missing participants', v_count;
    else
      raise notice 'PASS|orphan|no submission-to-participant orphans';
    end if;
  end if;

  if to_regclass('public.submission_files') is not null
     and to_regclass('public.submissions') is not null then
    execute $sql$
      select count(*)
      from public.submission_files f
      left join public.submissions s on s.id = f.submission_id
      where s.id is null
    $sql$ into v_count;
    if v_count > 0 then
      raise notice 'BLOCKER|orphan|% submission files reference missing submissions', v_count;
    else
      raise notice 'PASS|orphan|no submission-file orphans';
    end if;
  end if;

  if to_regclass('public.submissions') is not null
     and to_regclass('public.session_participants') is not null
     and exists (select 1 from information_schema.columns where table_schema='public' and table_name='submissions' and column_name='organization_id')
     and exists (select 1 from information_schema.columns where table_schema='public' and table_name='session_participants' and column_name='organization_id') then
    execute $sql$
      select count(*)
      from public.submissions s
      join public.session_participants p on p.id = s.participant_id
      where s.organization_id is distinct from p.organization_id
    $sql$ into v_count;
    if v_count > 0 then
      raise notice 'BLOCKER|cross_organization|% submissions and participants have different organizations', v_count;
    else
      raise notice 'PASS|cross_organization|submission ownership is consistent';
    end if;
  end if;

  if to_regclass('public.submission_files') is not null
     and exists (select 1 from information_schema.columns where table_schema='public' and table_name='submission_files' and column_name='cloud_object_path') then
    select exists (
      select 1 from information_schema.columns
      where table_schema = 'public' and table_name = 'submission_files'
        and column_name = 'source_mode'
    ) into v_has_source_mode;
    if v_has_source_mode then
      execute $sql$
        select count(*)
        from public.submission_files
        where source_mode = 'PublicCloud'
          and cloud_object_path is not null
          and (
            cloud_object_path like '/%'
            or cloud_object_path like '%..%'
            or cloud_object_path !~ '^[A-Fa-f0-9-]+/public-submissions/[A-Fa-f0-9-]+/[A-Fa-f0-9-]+/[A-Fa-f0-9-]+\.[A-Za-z0-9]+$'
          )
      $sql$ into v_count;
      if v_count > 0 then
        raise notice 'BLOCKER|storage_path|% PublicCloud submission object paths are invalid or unsafe', v_count;
      else
        raise notice 'PASS|storage_path|PublicCloud submission object paths are structurally safe';
      end if;
    else
      execute 'select count(*) from public.submission_files where cloud_object_path is not null' into v_count;
      if v_count > 0 then
        raise notice 'WARNING|storage_path|source_mode is absent; % legacy object paths are intentionally not judged by the PublicCloud path rule', v_count;
      else
        raise notice 'PASS|storage_path|no legacy object paths require classification';
      end if;
    end if;
  end if;

  for v_table, v_column_name in
    select table_name, column_name
    from information_schema.columns
    where table_schema = 'public' and column_name in ('cloud_id','cloud_entity_id')
    order by table_name, column_name
  loop
    execute format(
      'select count(*) from (select %I from public.%I where %I is not null group by %I having count(*) > 1) d',
      v_column_name, v_table, v_column_name, v_column_name)
      into v_count;
    if v_count > 0 then
      raise notice 'BLOCKER|duplicate_cloud_id|public.%.% has % duplicate groups', v_table, v_column_name, v_count;
    else
      raise notice 'PASS|duplicate_cloud_id|public.%.% has no duplicates', v_table, v_column_name;
    end if;
  end loop;

  if to_regclass('public.examtransfer_cloud_meta') is null then
    raise notice 'WARNING|schema_version|examtransfer_cloud_meta is not installed yet';
  else
    execute 'select coalesce(max(schema_version),0) from public.examtransfer_cloud_meta' into v_count;
    if v_count < 18 then
      raise notice 'WARNING|schema_version|current=% required=18', v_count;
    else
      raise notice 'PASS|schema_version|current=% required=18', v_count;
    end if;
  end if;

  foreach v_column in array array[
    'join_public_session(uuid,text,text,text,jsonb)',
    'init_public_submission(uuid,text,text,bigint,text)',
    'finalize_public_submission(uuid,text)',
    'get_examtransfer_cloud_capabilities()',
    'add_public_participant_extra_time(uuid,uuid,integer,text,uuid)'
  ] loop
    if to_regprocedure('public.' || v_column) is null then
      raise notice 'WARNING|rpc|public.% is not installed', v_column;
    else
      raise notice 'PASS|rpc|public.% exists', v_column;
    end if;
  end loop;

  if to_regclass('storage.buckets') is null then
    raise notice 'WARNING|bucket|storage.buckets is unavailable';
  else
    foreach v_column in array array[
      'exam-archives','submission-archives','public-submission-archives','report-exports','backup-archives'
    ] loop
      execute 'select exists (select 1 from storage.buckets where id = $1)' using v_column into v_exists;
      if v_exists then
        raise notice 'PASS|bucket|% exists', v_column;
      else
        raise notice 'WARNING|bucket|% is absent', v_column;
      end if;
    end loop;
  end if;

  for v_table in
    select c.relname || '.' || t.tgname
    from pg_trigger t
    join pg_class c on c.oid = t.tgrelid
    join pg_namespace n on n.oid = c.relnamespace
    where n.nspname = 'public'
      and c.relname in ('submissions','submission_files')
      and not t.tgisinternal
    order by c.relname, t.tgname
  loop
    raise notice 'PASS|trigger|%', v_table;
  end loop;

  if not v_history_available then
    raise notice 'WARNING|migration_history|supabase_migrations.schema_migrations is unavailable to this role';
  else
    execute 'select count(*) from supabase_migrations.schema_migrations' into v_count;
    raise notice 'PASS|migration_history|rows=%', v_count;
    execute $sql$
      select count(*) from supabase_migrations.schema_migrations
      where version in ('20260722141147','20260722161450','20260723043859')
    $sql$ into v_count;
    raise notice 'PASS|migration_history|known PublicCloud migrations present=%', v_count;
  end if;

  raise notice 'PASS|preflight|completed; review WARNING and stop on every BLOCKER';
end
$preflight$;
