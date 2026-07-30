-- ExamTransfer full cloud projection for the desktop product.
-- SQLite remains authoritative while a LAN exam is active. This schema stores
-- synchronized metadata and Storage object pointers for backup/reporting.

create extension if not exists pgcrypto;

-- Expand the tables created by the initial migration without breaking an
-- existing Supabase project.
alter table public.classes add column if not exists description text;
alter table public.classes add column if not exists created_by uuid;

alter table public.exams add column if not exists description text;
alter table public.exams add column if not exists file_rule_json jsonb not null default '{}'::jsonb;
alter table public.exams add column if not exists created_by uuid;

alter table public.exam_sessions add column if not exists host_device_id text;
alter table public.exam_sessions add column if not exists planned_start_at timestamptz;
alter table public.exam_sessions add column if not exists settings_json jsonb not null default '{}'::jsonb;
alter table public.exam_sessions add column if not exists auto_approve boolean not null default false;
alter table public.exam_sessions add column if not exists capacity integer;
alter table public.exam_sessions add column if not exists accepting_participants boolean not null default true;
alter table public.exam_sessions add column if not exists sequence bigint not null default 0;

alter table public.session_participants add column if not exists user_id uuid;
alter table public.session_participants add column if not exists class_name text;
alter table public.session_participants add column if not exists machine_name text;
alter table public.session_participants add column if not exists ip_address text;
alter table public.session_participants add column if not exists app_version text;
alter table public.session_participants add column if not exists joined_at timestamptz;
alter table public.session_participants add column if not exists approved_at timestamptz;
alter table public.session_participants add column if not exists last_seen_at timestamptz;
alter table public.session_participants add column if not exists download_status text;
alter table public.session_participants add column if not exists submission_status text;
alter table public.session_participants add column if not exists extra_time_minutes integer not null default 0;
alter table public.session_participants add column if not exists resubmit_allowed boolean not null default false;
alter table public.session_participants add column if not exists resubmit_reason text;
alter table public.session_participants add column if not exists capability_json jsonb;

alter table public.submissions add column if not exists idempotency_key text;
alter table public.submissions add column if not exists client_submitted_at timestamptz;
alter table public.submissions add column if not exists is_official boolean not null default false;
alter table public.submissions add column if not exists receipt_signature text;
alter table public.submissions add column if not exists teacher_reject_reason text;
alter table public.submissions add column if not exists client_note text;

alter table public.grades add column if not exists grader_id uuid;

alter table public.audit_logs add column if not exists ip_address text;
alter table public.audit_logs add column if not exists before_json jsonb;
alter table public.audit_logs add column if not exists after_json jsonb;
alter table public.audit_logs add column if not exists updated_at timestamptz not null default now();

create table if not exists public.class_members (
  id uuid primary key,
  organization_id uuid references public.organizations(id) on delete restrict,
  class_id uuid not null references public.classes(id) on delete cascade,
  user_id uuid,
  student_code text not null,
  display_name text not null,
  email text,
  metadata_json jsonb,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  unique(class_id, student_code)
);

create table if not exists public.exam_files (
  id uuid primary key,
  organization_id uuid references public.organizations(id) on delete restrict,
  exam_id uuid not null references public.exams(id) on delete cascade,
  version integer not null,
  name text not null,
  stored_name text,
  mime_type text,
  size_bytes bigint not null,
  sha256 text not null,
  transfer_status text,
  sync_status text,
  cloud_object_path text,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  unique(exam_id, version, name)
);

create table if not exists public.submission_files (
  id uuid primary key,
  organization_id uuid references public.organizations(id) on delete restrict,
  submission_id uuid not null references public.submissions(id) on delete cascade,
  client_file_id text,
  name text not null,
  stored_name text,
  mime_type text,
  size_bytes bigint not null,
  sha256 text not null,
  transfer_status text,
  sync_status text,
  cloud_object_path text,
  created_at timestamptz not null,
  updated_at timestamptz not null
);

create table if not exists public.rubric_scores (
  id uuid primary key,
  organization_id uuid references public.organizations(id) on delete restrict,
  grade_id uuid not null references public.grades(id) on delete cascade,
  criterion_key text not null,
  title text not null,
  score numeric not null,
  max_score numeric not null,
  comment text,
  sort_order integer not null default 0,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  unique(grade_id, criterion_key)
);

create table if not exists public.graded_attachments (
  id uuid primary key,
  organization_id uuid references public.organizations(id) on delete restrict,
  grade_id uuid not null references public.grades(id) on delete cascade,
  name text not null,
  size_bytes bigint not null,
  sha256 text not null,
  mime_type text,
  cloud_object_path text,
  created_at timestamptz not null,
  updated_at timestamptz not null
);

create table if not exists public.control_policies (
  id uuid primary key,
  organization_id uuid references public.organizations(id) on delete restrict,
  session_id uuid not null references public.exam_sessions(id) on delete cascade,
  version integer not null,
  status text not null,
  policy_json jsonb not null default '{}'::jsonb,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  unique(session_id, version)
);

create table if not exists public.violations (
  id uuid primary key,
  organization_id uuid references public.organizations(id) on delete restrict,
  session_id uuid not null references public.exam_sessions(id) on delete cascade,
  participant_id uuid not null references public.session_participants(id) on delete cascade,
  type text not null,
  severity text not null,
  occurred_at timestamptz not null,
  payload_json jsonb,
  handled_at timestamptz,
  handled_by uuid,
  created_at timestamptz not null,
  updated_at timestamptz not null
);

create table if not exists public.export_jobs (
  id uuid primary key,
  organization_id uuid references public.organizations(id) on delete restrict,
  session_id uuid not null references public.exam_sessions(id) on delete cascade,
  status text not null,
  progress double precision not null default 0,
  output_file_name text,
  cloud_object_path text,
  completed_at timestamptz,
  error text,
  created_at timestamptz not null,
  updated_at timestamptz not null
);

create table if not exists public.backups (
  id uuid primary key,
  organization_id uuid references public.organizations(id) on delete restrict,
  file_name text not null,
  size_bytes bigint not null,
  sha256 text not null,
  schema_version text not null,
  encrypted boolean not null default false,
  status text not null,
  cloud_object_path text,
  error text,
  created_at timestamptz not null,
  updated_at timestamptz not null
);

create index if not exists ix_class_members_class on public.class_members(class_id);
create index if not exists ix_exam_files_exam_version on public.exam_files(exam_id, version);
create index if not exists ix_sessions_status on public.exam_sessions(status, planned_start_at);
create index if not exists ix_participants_session_status on public.session_participants(session_id, status);
create index if not exists ix_submissions_session_participant on public.submissions(session_id, participant_id, attempt_number);
create index if not exists ix_submission_files_submission on public.submission_files(submission_id);
create index if not exists ix_grades_status on public.grades(status, updated_at desc);
create index if not exists ix_violations_session_time on public.violations(session_id, occurred_at desc);
create index if not exists ix_export_jobs_session on public.export_jobs(session_id, created_at desc);
create index if not exists ix_audit_logs_session_time on public.audit_logs(session_id, created_at desc);

-- Apply tenant policies to every cloud projection. The service-role key used by
-- the trusted local backend bypasses RLS. Authenticated dashboard users remain
-- scoped to the organization in public.profiles.
do $$
declare
  table_name text;
  read_policy text;
  write_policy text;
begin
  foreach table_name in array array[
    'class_members', 'exam_files', 'submission_files', 'rubric_scores',
    'graded_attachments', 'control_policies', 'violations', 'export_jobs',
    'backups'
  ]
  loop
    execute format('alter table public.%I enable row level security', table_name);
    read_policy := 'organization read ' || table_name;
    write_policy := 'organization write ' || table_name;

    if not exists (
      select 1 from pg_policies
      where schemaname = 'public' and tablename = table_name and policyname = read_policy
    ) then
      execute format(
        'create policy %I on public.%I for select using (organization_id = public.current_organization_id())',
        read_policy, table_name);
    end if;

    if not exists (
      select 1 from pg_policies
      where schemaname = 'public' and tablename = table_name and policyname = write_policy
    ) then
      execute format(
        'create policy %I on public.%I for all using (organization_id = public.current_organization_id()) with check (organization_id = public.current_organization_id())',
        write_policy, table_name);
    end if;
  end loop;
end $$;

-- Complete missing policies for the original tables in an idempotent manner.
do $$
declare
  table_name text;
  read_policy text;
  write_policy text;
begin
  foreach table_name in array array['classes','exams','exam_sessions','session_participants','submissions','grades']
  loop
    execute format('alter table public.%I enable row level security', table_name);
    read_policy := 'organization read ' || table_name;
    write_policy := 'organization write ' || table_name;

    if not exists (
      select 1 from pg_policies
      where schemaname = 'public' and tablename = table_name and cmd = 'SELECT'
    ) then
      execute format(
        'create policy %I on public.%I for select using (organization_id = public.current_organization_id())',
        read_policy, table_name);
    end if;

    if not exists (
      select 1 from pg_policies
      where schemaname = 'public' and tablename = table_name and cmd = 'ALL'
    ) then
      execute format(
        'create policy %I on public.%I for all using (organization_id = public.current_organization_id()) with check (organization_id = public.current_organization_id())',
        write_policy, table_name);
    end if;
  end loop;
end $$;

alter table public.audit_logs enable row level security;
do $$
begin
  if not exists (
    select 1 from pg_policies
    where schemaname = 'public' and tablename = 'audit_logs' and cmd = 'SELECT'
  ) then
    execute 'create policy "organization read audit_logs" on public.audit_logs for select using (organization_id = public.current_organization_id())';
  end if;
end $$;

-- Cloud files are private and are uploaded by the trusted backend. No broad
-- authenticated Storage policy is created here; signed downloads can be added
-- later by a dedicated cloud API without exposing another organization's data.
insert into storage.buckets (id, name, public) values
  ('backup-archives','backup-archives',false)
on conflict (id) do nothing;
