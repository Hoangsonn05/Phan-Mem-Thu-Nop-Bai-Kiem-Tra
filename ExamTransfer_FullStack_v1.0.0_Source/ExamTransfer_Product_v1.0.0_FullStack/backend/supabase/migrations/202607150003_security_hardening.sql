-- ExamTransfer Supabase production hardening.
-- Run after 202607110001 and 202607140002.

create table if not exists public.examtransfer_cloud_meta (
  id integer primary key check (id = 1),
  schema_version integer not null,
  updated_at timestamptz not null default now()
);

insert into public.examtransfer_cloud_meta(id, schema_version)
values (1, 3)
on conflict (id) do update
set schema_version = excluded.schema_version,
    updated_at = now();

-- Fail explicitly instead of silently assigning tenant-less cloud rows.
do $$
declare
  table_name text;
  null_count bigint;
begin
  foreach table_name in array array[
    'profiles','classes','class_members','exams','exam_files','exam_sessions',
    'session_participants','submissions','submission_files','grades',
    'rubric_scores','graded_attachments','control_policies','violations',
    'export_jobs','backups','audit_logs'
  ]
  loop
    execute format(
      'select count(*) from public.%I where organization_id is null',
      table_name)
      into null_count;
    if null_count > 0 then
      raise exception
        'Cannot harden %.organization_id: % rows are NULL. Assign an organization before running this migration.',
        table_name,
        null_count;
    end if;
  end loop;
end $$;

alter table public.profiles alter column organization_id set not null;
alter table public.classes alter column organization_id set not null;
alter table public.class_members alter column organization_id set not null;
alter table public.exams alter column organization_id set not null;
alter table public.exam_files alter column organization_id set not null;
alter table public.exam_sessions alter column organization_id set not null;
alter table public.session_participants alter column organization_id set not null;
alter table public.submissions alter column organization_id set not null;
alter table public.submission_files alter column organization_id set not null;
alter table public.grades alter column organization_id set not null;
alter table public.rubric_scores alter column organization_id set not null;
alter table public.graded_attachments alter column organization_id set not null;
alter table public.control_policies alter column organization_id set not null;
alter table public.violations alter column organization_id set not null;
alter table public.export_jobs alter column organization_id set not null;
alter table public.backups alter column organization_id set not null;
alter table public.audit_logs alter column organization_id set not null;

create index if not exists ix_profiles_organization on public.profiles(organization_id);
create index if not exists ix_classes_organization on public.classes(organization_id);
create index if not exists ix_class_members_organization on public.class_members(organization_id);
create index if not exists ix_exams_organization on public.exams(organization_id);
create index if not exists ix_exam_files_organization on public.exam_files(organization_id);
create index if not exists ix_exam_sessions_organization on public.exam_sessions(organization_id);
create index if not exists ix_session_participants_organization on public.session_participants(organization_id);
create index if not exists ix_submissions_organization on public.submissions(organization_id);
create index if not exists ix_submission_files_organization on public.submission_files(organization_id);
create index if not exists ix_grades_organization on public.grades(organization_id);
create index if not exists ix_rubric_scores_organization on public.rubric_scores(organization_id);
create index if not exists ix_graded_attachments_organization on public.graded_attachments(organization_id);
create index if not exists ix_control_policies_organization on public.control_policies(organization_id);
create index if not exists ix_violations_organization on public.violations(organization_id);
create index if not exists ix_export_jobs_organization on public.export_jobs(organization_id);
create index if not exists ix_backups_organization on public.backups(organization_id);
create index if not exists ix_audit_logs_organization on public.audit_logs(organization_id);

create or replace function public.current_organization_id()
returns uuid
language sql
stable
security definer
set search_path = ''
as $$
  select p.organization_id
  from public.profiles as p
  where p.id = auth.uid()
$$;

create or replace function public.current_examtransfer_role()
returns text
language sql
stable
security definer
set search_path = ''
as $$
  select p.role
  from public.profiles as p
  where p.id = auth.uid()
$$;

revoke all on function public.current_organization_id() from public;
revoke all on function public.current_examtransfer_role() from public;
grant execute on function public.current_organization_id() to authenticated;
grant execute on function public.current_examtransfer_role() to authenticated;

-- Rebuild policies under stable, predictable names.
do $$
declare
  table_name text;
  policy_record record;
begin
  foreach table_name in array array[
    'organizations','profiles','classes','class_members','exams','exam_files',
    'exam_sessions','session_participants','submissions','submission_files',
    'grades','rubric_scores','graded_attachments','control_policies',
    'violations','export_jobs','backups','audit_logs','examtransfer_cloud_meta'
  ]
  loop
    execute format('alter table public.%I enable row level security', table_name);
    for policy_record in
      select policyname
      from pg_policies
      where schemaname = 'public' and tablename = table_name
    loop
      execute format(
        'drop policy if exists %I on public.%I',
        policy_record.policyname,
        table_name);
    end loop;
  end loop;
end $$;

create policy "organization_select_own"
on public.organizations
for select
to authenticated
using (id = (select public.current_organization_id()));

create policy "profile_select_scope"
on public.profiles
for select
to authenticated
using (
  id = auth.uid()
  or (
    organization_id = (select public.current_organization_id())
    and (select public.current_examtransfer_role()) = 'Admin'
  )
);

create policy "profile_update_self"
on public.profiles
for update
to authenticated
using (id = auth.uid())
with check (
  id = auth.uid()
  and organization_id = (select public.current_organization_id())
);

create policy "cloud_meta_read"
on public.examtransfer_cloud_meta
for select
to authenticated
using (true);

do $$
declare
  table_name text;
begin
  foreach table_name in array array[
    'classes','class_members','exams','exam_files','exam_sessions',
    'session_participants','submissions','submission_files','grades',
    'rubric_scores','graded_attachments','control_policies','violations',
    'export_jobs','backups'
  ]
  loop
    execute format(
      'create policy %I on public.%I for select to authenticated using (organization_id = (select public.current_organization_id()))',
      table_name || '_tenant_select',
      table_name);
    execute format(
      'create policy %I on public.%I for insert to authenticated with check (organization_id = (select public.current_organization_id()) and (select public.current_examtransfer_role()) in (''Admin'',''Teacher''))',
      table_name || '_tenant_insert',
      table_name);
    execute format(
      'create policy %I on public.%I for update to authenticated using (organization_id = (select public.current_organization_id()) and (select public.current_examtransfer_role()) in (''Admin'',''Teacher'')) with check (organization_id = (select public.current_organization_id()) and (select public.current_examtransfer_role()) in (''Admin'',''Teacher''))',
      table_name || '_tenant_update',
      table_name);
    execute format(
      'create policy %I on public.%I for delete to authenticated using (organization_id = (select public.current_organization_id()) and (select public.current_examtransfer_role()) in (''Admin'',''Teacher''))',
      table_name || '_tenant_delete',
      table_name);
  end loop;
end $$;

create policy "audit_tenant_select"
on public.audit_logs
for select
to authenticated
using (organization_id = (select public.current_organization_id()));

-- Explicit grants: RLS remains the row-level enforcement layer.
revoke all on all tables in schema public from anon;
grant usage on schema public to authenticated;
grant select on public.organizations, public.profiles, public.examtransfer_cloud_meta to authenticated;
grant update(display_name) on public.profiles to authenticated;
grant select, insert, update, delete on
  public.classes,
  public.class_members,
  public.exams,
  public.exam_files,
  public.exam_sessions,
  public.session_participants,
  public.submissions,
  public.submission_files,
  public.grades,
  public.rubric_scores,
  public.graded_attachments,
  public.control_policies,
  public.violations,
  public.export_jobs,
  public.backups
to authenticated;
grant select on public.audit_logs to authenticated;

-- All buckets stay private. Migration 202607150005 adds organization-scoped
-- authenticated Storage policies for UserSession mode. TrustedServer mode may
-- still use a server-only secret key and therefore bypasses RLS.
update storage.buckets
set public = false
where id in (
  'exam-archives',
  'submission-archives',
  'report-exports',
  'backup-archives',
  'application-releases'
);
