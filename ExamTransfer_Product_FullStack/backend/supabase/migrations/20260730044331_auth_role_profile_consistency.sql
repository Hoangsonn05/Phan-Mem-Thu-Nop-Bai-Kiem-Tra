begin;
-- public.profiles.role is the application authorization source. A profile
-- whose role conflicts with its student-only identity fields must not receive
-- either Student or staff authority until its data is repaired.
create or replace function public.current_examtransfer_role()
returns text
language sql
stable
security definer
set search_path = ''
as $function$
  select case
    when p.is_active is distinct from true then null
    when p.role = 'Student'
      and nullif(btrim(p.username), '') is not null
      and nullif(btrim(p.student_code), '') is not null
      and nullif(btrim(p.display_name), '') is not null
      and p.date_of_birth is not null
      and lower(btrim(p.username)) = lower(btrim(p.student_code))
      then 'Student'
    when p.role in ('Admin', 'Teacher')
      and nullif(btrim(p.student_code), '') is null
      then p.role
    else null
  end
  from public.profiles as p
  where p.id = (select auth.uid())
$function$;
revoke all on function public.current_examtransfer_role() from public, anon;
grant execute on function public.current_examtransfer_role() to authenticated;
-- Tenant-only policies must fail closed under the same role contract. Without
-- this guard, a conflicting profile could still satisfy organization filters
-- even though current_examtransfer_role() returned NULL.
create or replace function public.current_organization_id()
returns uuid
language sql
stable
security definer
set search_path = ''
as $function$
  select p.organization_id
  from public.profiles as p
  where p.id = (select auth.uid())
    and (select public.current_examtransfer_role()) is not null
$function$;
revoke all on function public.current_organization_id() from public, anon;
grant execute on function public.current_organization_id() to authenticated;
-- NOT VALID keeps deployment possible while legacy rows are repaired, but
-- PostgreSQL enforces the constraint for every new or changed row.
do $migration$
begin
  if not exists (
    select 1
    from pg_constraint
    where conname = 'profiles_privileged_role_has_no_student_code_check'
      and conrelid = 'public.profiles'::regclass
  ) then
    alter table public.profiles
      add constraint profiles_privileged_role_has_no_student_code_check
      check (
        role not in ('Admin', 'Teacher')
        or nullif(btrim(student_code), '') is null
      ) not valid;
  end if;
end
$migration$;
do $migration$
begin
  if not exists (
    select 1
    from public.profiles
    where role in ('Admin', 'Teacher')
      and nullif(btrim(student_code), '') is not null
  ) then
    alter table public.profiles
      validate constraint profiles_privileged_role_has_no_student_code_check;
  end if;
end
$migration$;
commit;
