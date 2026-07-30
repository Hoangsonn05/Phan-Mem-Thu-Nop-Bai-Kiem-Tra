begin;

alter table public.exams add column if not exists delivery_type text not null default 'FileSubmission';
alter table public.exams drop constraint if exists exams_delivery_type_check;
alter table public.exams add constraint exams_delivery_type_check check (delivery_type in ('FileSubmission', 'MultipleChoice'));

create table if not exists public.quiz_questions (
  id uuid primary key,
  organization_id uuid not null references public.organizations(id) on delete restrict,
  exam_id uuid not null references public.exams(id) on delete cascade,
  version integer not null check (version > 0),
  sort_order integer not null check (sort_order > 0),
  question_text text not null check (length(question_text) between 1 and 5000),
  points numeric(10,2) not null check (points > 0),
  multiple boolean not null default false,
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  unique (exam_id, version, sort_order)
);

create table if not exists public.quiz_choices (
  id uuid primary key,
  organization_id uuid not null references public.organizations(id) on delete restrict,
  question_id uuid not null references public.quiz_questions(id) on delete cascade,
  sort_order integer not null check (sort_order > 0),
  choice_text text not null check (length(choice_text) between 1 and 5000),
  is_correct boolean not null default false,
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  unique (question_id, sort_order)
);

create table if not exists public.quiz_attempts (
  id uuid primary key,
  organization_id uuid not null references public.organizations(id) on delete restrict,
  session_id uuid not null references public.exam_sessions(id) on delete restrict,
  participant_id uuid not null references public.session_participants(id) on delete restrict,
  exam_version integer not null check (exam_version > 0), status text not null check (status in ('InProgress','Finalized')),
  started_at timestamptz not null, deadline_at timestamptz not null, finalized_at timestamptz,
  score numeric(10,2), max_score numeric(10,2) not null check (max_score > 0),
  snapshot_json jsonb not null, finalize_idempotency_key text,
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  unique (session_id, participant_id)
);

create table if not exists public.quiz_answers (
  id uuid primary key,
  organization_id uuid not null references public.organizations(id) on delete restrict,
  attempt_id uuid not null references public.quiz_attempts(id) on delete cascade,
  question_id uuid not null references public.quiz_questions(id) on delete restrict,
  choice_ids jsonb not null default '[]'::jsonb, revision bigint not null check (revision > 0),
  client_updated_at timestamptz not null,
  created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
  unique (attempt_id, question_id)
);

create index if not exists ix_quiz_questions_org_exam_version on public.quiz_questions(organization_id, exam_id, version, sort_order);
create index if not exists ix_quiz_choices_org_question on public.quiz_choices(organization_id, question_id, sort_order);
create index if not exists ix_quiz_attempts_org_participant on public.quiz_attempts(organization_id, participant_id, status);
create index if not exists ix_quiz_answers_org_attempt on public.quiz_answers(organization_id, attempt_id, revision);

alter table public.quiz_questions enable row level security; alter table public.quiz_questions force row level security;
alter table public.quiz_choices enable row level security; alter table public.quiz_choices force row level security;
alter table public.quiz_attempts enable row level security; alter table public.quiz_attempts force row level security;
alter table public.quiz_answers enable row level security; alter table public.quiz_answers force row level security;

create policy quiz_questions_staff_all on public.quiz_questions for all to authenticated
  using (organization_id = (select public.current_organization_id()) and (select public.current_examtransfer_role()) in ('Admin','Teacher'))
  with check (organization_id = (select public.current_organization_id()) and (select public.current_examtransfer_role()) in ('Admin','Teacher'));
create policy quiz_choices_staff_all on public.quiz_choices for all to authenticated
  using (organization_id = (select public.current_organization_id()) and (select public.current_examtransfer_role()) in ('Admin','Teacher'))
  with check (organization_id = (select public.current_organization_id()) and (select public.current_examtransfer_role()) in ('Admin','Teacher'));
create policy quiz_attempts_staff_all on public.quiz_attempts for all to authenticated
  using (organization_id = (select public.current_organization_id()) and (select public.current_examtransfer_role()) in ('Admin','Teacher'))
  with check (organization_id = (select public.current_organization_id()) and (select public.current_examtransfer_role()) in ('Admin','Teacher'));
create policy quiz_attempts_student_own on public.quiz_attempts for select to authenticated
  using (organization_id = (select public.current_organization_id()) and exists (select 1 from public.session_participants p where p.id = participant_id and p.user_id = (select auth.uid())));
create policy quiz_answers_staff_all on public.quiz_answers for all to authenticated
  using (organization_id = (select public.current_organization_id()) and (select public.current_examtransfer_role()) in ('Admin','Teacher'))
  with check (organization_id = (select public.current_organization_id()) and (select public.current_examtransfer_role()) in ('Admin','Teacher'));
create policy quiz_answers_student_own on public.quiz_answers for select to authenticated
  using (organization_id = (select public.current_organization_id()) and exists (select 1 from public.quiz_attempts a join public.session_participants p on p.id = a.participant_id where a.id = attempt_id and p.user_id = (select auth.uid())));

grant select, insert, update, delete on public.quiz_questions, public.quiz_choices to authenticated;
grant select, insert, update, delete on public.quiz_attempts, public.quiz_answers to authenticated;

commit;
