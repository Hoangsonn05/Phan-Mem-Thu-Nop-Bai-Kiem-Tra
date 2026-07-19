-- ExamTransfer UserSession Storage policies and audit write policy.
-- This migration enables desktop/local-server synchronization with a
-- publishable key plus a signed-in Teacher/Admin JWT. TrustedServer mode still
-- works because Supabase secret/service-role credentials bypass RLS.

create policy "audit_tenant_insert"
on public.audit_logs
for insert
to authenticated
with check (
  organization_id = (select public.current_organization_id())
  and (select public.current_examtransfer_role()) in ('Admin','Teacher')
);

grant insert on public.audit_logs to authenticated;

-- Keep all application buckets private. Object names are always prefixed by
-- organization UUID, then environment, for example:
-- <organization>/<environment>/exam-files/<id>/<file-name>
update storage.buckets
set public = false
where id in (
  'exam-archives',
  'submission-archives',
  'report-exports',
  'backup-archives',
  'application-releases'
);

do $$
declare
  policy_record record;
begin
  for policy_record in
    select policyname
    from pg_policies
    where schemaname = 'storage'
      and tablename = 'objects'
      and policyname like 'examtransfer_%'
  loop
    execute format(
      'drop policy if exists %I on storage.objects',
      policy_record.policyname);
  end loop;
end $$;

create policy "examtransfer_storage_select"
on storage.objects
for select
to authenticated
using (
  bucket_id in (
    'exam-archives',
    'submission-archives',
    'report-exports',
    'backup-archives'
  )
  and (storage.foldername(name))[1]
      = (select public.current_organization_id())::text
);

create policy "examtransfer_storage_insert"
on storage.objects
for insert
to authenticated
with check (
  bucket_id in (
    'exam-archives',
    'submission-archives',
    'report-exports',
    'backup-archives'
  )
  and (storage.foldername(name))[1]
      = (select public.current_organization_id())::text
  and (select public.current_examtransfer_role()) in ('Admin','Teacher')
);

create policy "examtransfer_storage_update"
on storage.objects
for update
to authenticated
using (
  bucket_id in (
    'exam-archives',
    'submission-archives',
    'report-exports',
    'backup-archives'
  )
  and (storage.foldername(name))[1]
      = (select public.current_organization_id())::text
  and (select public.current_examtransfer_role()) in ('Admin','Teacher')
)
with check (
  bucket_id in (
    'exam-archives',
    'submission-archives',
    'report-exports',
    'backup-archives'
  )
  and (storage.foldername(name))[1]
      = (select public.current_organization_id())::text
  and (select public.current_examtransfer_role()) in ('Admin','Teacher')
);

create policy "examtransfer_storage_delete"
on storage.objects
for delete
to authenticated
using (
  bucket_id in (
    'exam-archives',
    'submission-archives',
    'report-exports',
    'backup-archives'
  )
  and (storage.foldername(name))[1]
      = (select public.current_organization_id())::text
  and (select public.current_examtransfer_role()) in ('Admin','Teacher')
);

update public.examtransfer_cloud_meta
set schema_version = 5,
    updated_at = now()
where id = 1;
