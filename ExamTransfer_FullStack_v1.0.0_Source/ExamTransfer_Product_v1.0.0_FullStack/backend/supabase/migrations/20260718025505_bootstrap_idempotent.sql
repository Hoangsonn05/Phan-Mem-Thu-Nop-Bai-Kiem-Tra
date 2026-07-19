-- Make bootstrap_examtransfer_organization idempotent.
-- When a profile already exists for the calling user, return the existing
-- organization_id instead of raising an exception. This allows the bootstrap
-- script to be re-run safely (e.g. to retrieve the OrganizationId after a
-- successful first run).

create or replace function public.bootstrap_examtransfer_organization(
  organization_name text,
  display_name text default ''
)
returns uuid
language plpgsql
security definer
set search_path = ''
as $$
declare
  user_id         uuid := auth.uid();
  organization_id uuid;
begin
  if user_id is null then
    raise exception 'Authentication required';
  end if;

  -- Idempotent: if profile already exists, return the existing organization_id.
  select p.organization_id
  into organization_id
  from public.profiles as p
  where p.id = user_id;

  if found then
    return organization_id;
  end if;

  if length(trim(organization_name)) < 2 then
    raise exception 'Organization name is required';
  end if;

  insert into public.organizations(name)
  values (trim(organization_name))
  returning id into organization_id;

  insert into public.profiles(
    id,
    organization_id,
    display_name,
    role)
  values (
    user_id,
    organization_id,
    coalesce(nullif(trim(display_name), ''), 'Administrator'),
    'Admin');

  return organization_id;
end;
$$;

-- Re-apply grants (create or replace does not reset them, but be explicit).
revoke all on function public.bootstrap_examtransfer_organization(text, text) from public;
grant execute on function public.bootstrap_examtransfer_organization(text, text) to authenticated;

update public.examtransfer_cloud_meta
set schema_version = 10,
    updated_at = now()
where id = 1;
