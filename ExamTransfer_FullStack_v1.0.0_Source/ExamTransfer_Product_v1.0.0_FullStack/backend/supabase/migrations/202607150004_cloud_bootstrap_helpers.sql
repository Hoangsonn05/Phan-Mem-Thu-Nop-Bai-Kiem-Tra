-- Optional tenant bootstrap helper for a signed-in Supabase user.
-- It creates exactly one organization/profile for the caller when they do not
-- already have a profile. Disable public sign-up in Supabase Auth if only
-- administrators should be able to provision organizations.

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
  user_id uuid := auth.uid();
  organization_id uuid;
begin
  if user_id is null then
    raise exception 'Authentication required';
  end if;

  if exists (select 1 from public.profiles where id = user_id) then
    raise exception 'Profile already exists';
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

revoke all on function public.bootstrap_examtransfer_organization(text, text) from public;
grant execute on function public.bootstrap_examtransfer_organization(text, text) to authenticated;

update public.examtransfer_cloud_meta
set schema_version = 4,
    updated_at = now()
where id = 1;
