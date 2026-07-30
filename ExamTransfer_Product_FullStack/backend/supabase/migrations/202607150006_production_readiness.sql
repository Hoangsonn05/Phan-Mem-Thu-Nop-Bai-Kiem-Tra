-- Final production-readiness constraints for ExamTransfer cloud projection.

-- Profiles are cloud operators. Student identities remain session participants
-- in the local-first exam model and do not require Supabase Auth accounts.
alter table public.profiles
  drop constraint if exists profiles_role_check;
alter table public.profiles
  add constraint profiles_role_check
  check (role in ('Admin','Teacher'));

-- Guard against duplicate cloud identities and common query patterns.
create unique index if not exists ux_profiles_organization_id
  on public.profiles(organization_id, id);
create index if not exists ix_exam_sessions_org_status_updated
  on public.exam_sessions(organization_id, status, updated_at desc);
create index if not exists ix_submissions_org_session_updated
  on public.submissions(organization_id, session_id, updated_at desc);
create index if not exists ix_audit_logs_org_created
  on public.audit_logs(organization_id, created_at desc);
create index if not exists ix_backups_org_created
  on public.backups(organization_id, created_at desc);

-- Keep bootstrap callable only by authenticated users; deployment owners should
-- disable public sign-up when self-service organization creation is undesired.
revoke all on function public.bootstrap_examtransfer_organization(text, text) from public;
grant execute on function public.bootstrap_examtransfer_organization(text, text) to authenticated;

update public.examtransfer_cloud_meta
set schema_version = 6,
    updated_at = now()
where id = 1;
