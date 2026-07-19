-- ExamTransfer cloud schema. Local SQLite remains authoritative during active LAN sessions.
create extension if not exists pgcrypto;

create table if not exists public.organizations (
  id uuid primary key default gen_random_uuid(),
  name text not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create table if not exists public.profiles (
  id uuid primary key references auth.users(id) on delete cascade,
  organization_id uuid references public.organizations(id) on delete restrict,
  display_name text not null default '',
  role text not null check (role in ('Admin','Teacher')),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create table if not exists public.classes (
  id uuid primary key,
  organization_id uuid references public.organizations(id) on delete restrict,
  name text not null,
  code text not null,
  school_year text not null,
  status text not null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  unique (organization_id, code, school_year)
);

create table if not exists public.exams (
  id uuid primary key,
  organization_id uuid references public.organizations(id) on delete restrict,
  class_id uuid references public.classes(id) on delete set null,
  title text not null,
  subject text not null,
  duration_minutes integer not null check (duration_minutes > 0),
  status text not null,
  version integer not null,
  created_at timestamptz not null,
  updated_at timestamptz not null
);

create table if not exists public.exam_sessions (
  id uuid primary key,
  organization_id uuid references public.organizations(id) on delete restrict,
  exam_id uuid references public.exams(id) on delete restrict,
  class_id uuid references public.classes(id) on delete set null,
  room_code text not null,
  status text not null,
  started_at timestamptz,
  ended_at timestamptz,
  created_at timestamptz not null,
  updated_at timestamptz not null
);

create table if not exists public.session_participants (
  id uuid primary key,
  organization_id uuid references public.organizations(id) on delete restrict,
  session_id uuid references public.exam_sessions(id) on delete cascade,
  student_code text not null,
  display_name text not null,
  device_id text,
  status text not null,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  unique(session_id, student_code)
);

create table if not exists public.submissions (
  id uuid primary key,
  organization_id uuid references public.organizations(id) on delete restrict,
  session_id uuid references public.exam_sessions(id) on delete restrict,
  participant_id uuid references public.session_participants(id) on delete restrict,
  attempt_number integer not null,
  status text not null,
  server_received_at timestamptz,
  deadline_at timestamptz not null,
  is_late boolean not null default false,
  receipt_code text,
  created_at timestamptz not null,
  updated_at timestamptz not null,
  unique(participant_id, attempt_number)
);

create table if not exists public.grades (
  id uuid primary key,
  organization_id uuid references public.organizations(id) on delete restrict,
  submission_id uuid references public.submissions(id) on delete cascade unique,
  status text not null,
  score numeric,
  max_score numeric not null,
  general_comment text,
  graded_at timestamptz,
  returned_at timestamptz,
  created_at timestamptz not null,
  updated_at timestamptz not null
);

create table if not exists public.audit_logs (
  id uuid primary key,
  organization_id uuid references public.organizations(id) on delete restrict,
  session_id uuid references public.exam_sessions(id) on delete set null,
  actor_id text,
  action text not null,
  entity_type text not null,
  entity_id text,
  trace_id text not null,
  created_at timestamptz not null
);

alter table public.organizations enable row level security;
alter table public.profiles enable row level security;
alter table public.classes enable row level security;
alter table public.exams enable row level security;
alter table public.exam_sessions enable row level security;
alter table public.session_participants enable row level security;
alter table public.submissions enable row level security;
alter table public.grades enable row level security;
alter table public.audit_logs enable row level security;

create or replace function public.current_organization_id()
returns uuid language sql stable security definer set search_path = public
as $$ select organization_id from public.profiles where id = auth.uid() $$;

create policy "organization read classes" on public.classes for select using (organization_id = public.current_organization_id());
create policy "organization write classes" on public.classes for all using (organization_id = public.current_organization_id()) with check (organization_id = public.current_organization_id());
create policy "organization read exams" on public.exams for select using (organization_id = public.current_organization_id());
create policy "organization write exams" on public.exams for all using (organization_id = public.current_organization_id()) with check (organization_id = public.current_organization_id());
create policy "organization read sessions" on public.exam_sessions for select using (organization_id = public.current_organization_id());
create policy "organization write sessions" on public.exam_sessions for all using (organization_id = public.current_organization_id()) with check (organization_id = public.current_organization_id());
create policy "organization read participants" on public.session_participants for select using (organization_id = public.current_organization_id());
create policy "organization write participants" on public.session_participants for all using (organization_id = public.current_organization_id()) with check (organization_id = public.current_organization_id());
create policy "organization read submissions" on public.submissions for select using (organization_id = public.current_organization_id());
create policy "organization write submissions" on public.submissions for all using (organization_id = public.current_organization_id()) with check (organization_id = public.current_organization_id());
create policy "organization read grades" on public.grades for select using (organization_id = public.current_organization_id());
create policy "organization write grades" on public.grades for all using (organization_id = public.current_organization_id()) with check (organization_id = public.current_organization_id());
create policy "organization read audits" on public.audit_logs for select using (organization_id = public.current_organization_id());

insert into storage.buckets (id, name, public) values
  ('exam-archives','exam-archives',false),
  ('submission-archives','submission-archives',false),
  ('report-exports','report-exports',false),
  ('application-releases','application-releases',false)
on conflict (id) do nothing;
