-- ExamTransfer application account auth and single-session lease support.

alter table public.profiles
  drop constraint if exists profiles_role_check;
alter table public.profiles
  add constraint profiles_role_check
  check (role in ('Admin','Teacher','Student'));

alter table public.profiles
  add column if not exists username text,
  add column if not exists student_code text,
  add column if not exists is_active boolean not null default true,
  add column if not exists last_login_at timestamptz;

create unique index if not exists ux_profiles_auth_user_id
  on public.profiles(id);
create unique index if not exists ux_profiles_username_norm
  on public.profiles(organization_id, lower(username))
  where username is not null and btrim(username) <> '';
create unique index if not exists ux_profiles_student_code_norm
  on public.profiles(organization_id, lower(student_code))
  where student_code is not null and btrim(student_code) <> '';

create table if not exists public.user_login_sessions (
  id uuid primary key default gen_random_uuid(),
  organization_id uuid not null references public.organizations(id) on delete restrict,
  user_id uuid not null references public.profiles(id) on delete cascade,
  device_id text not null,
  machine_name text not null default '',
  ip_address inet,
  token_hash text not null,
  encrypted_refresh_token text,
  created_at timestamptz not null default now(),
  last_seen_at timestamptz not null default now(),
  expires_at timestamptz not null,
  revoked_at timestamptz,
  revoke_reason text
);

create unique index if not exists ux_user_login_sessions_token_hash
  on public.user_login_sessions(token_hash);
create index if not exists ix_user_login_sessions_user_active
  on public.user_login_sessions(user_id, revoked_at, expires_at);
create index if not exists ix_user_login_sessions_org_user
  on public.user_login_sessions(organization_id, user_id);

alter table public.user_login_sessions enable row level security;

drop policy if exists "user_login_sessions_select_own_or_org_admin" on public.user_login_sessions;
create policy "user_login_sessions_select_own_or_org_admin"
on public.user_login_sessions
for select
to authenticated
using (
  user_id = auth.uid()
  or (
    organization_id = (select public.current_organization_id())
    and (select public.current_examtransfer_role()) in ('Admin','Teacher')
  )
);

drop policy if exists "profiles_select_student_self_or_org_staff" on public.profiles;
create policy "profiles_select_student_self_or_org_staff"
on public.profiles
for select
to authenticated
using (
  id = auth.uid()
  or (
    organization_id = (select public.current_organization_id())
    and (select public.current_examtransfer_role()) in ('Admin','Teacher')
  )
);

revoke insert, update, delete on public.user_login_sessions from anon, authenticated;
grant select on public.user_login_sessions to authenticated;

create or replace function public.claim_examtransfer_login_session(
  p_user_id uuid,
  p_device_id text,
  p_machine_name text,
  p_ip_address inet,
  p_token_hash text,
  p_encrypted_refresh_token text default null,
  p_lease_seconds integer default 120
)
returns table (
  id uuid,
  organization_id uuid,
  user_id uuid,
  device_id text,
  last_seen_at timestamptz,
  expires_at timestamptz
)
language plpgsql
security definer
set search_path = public
as $$
declare
  profile_row public.profiles%rowtype;
  existing public.user_login_sessions%rowtype;
  lease_until timestamptz;
begin
  if auth.uid() is null or auth.uid() <> p_user_id then
    raise exception 'DEVICE_MISMATCH' using errcode = '28000';
  end if;

  select * into profile_row
  from public.profiles
  where profiles.id = p_user_id
  for update;

  if not found or coalesce(profile_row.is_active, false) = false then
    raise exception 'ACCOUNT_INACTIVE' using errcode = '28000';
  end if;

  perform pg_advisory_xact_lock(hashtextextended(p_user_id::text, 0));

  update public.user_login_sessions
  set revoked_at = now(),
      revoke_reason = coalesce(revoke_reason, 'lease_expired')
  where user_login_sessions.user_id = p_user_id
    and revoked_at is null
    and expires_at <= now();

  select * into existing
  from public.user_login_sessions
  where user_login_sessions.user_id = p_user_id
    and revoked_at is null
    and expires_at > now()
  order by last_seen_at desc
  limit 1
  for update;

  if found and existing.device_id <> p_device_id then
    raise exception 'ACCOUNT_ALREADY_ACTIVE' using errcode = '23505';
  end if;

  lease_until := now() + make_interval(secs => greatest(coalesce(p_lease_seconds, 120), 30));

  if found then
    update public.user_login_sessions
    set token_hash = p_token_hash,
        encrypted_refresh_token = coalesce(p_encrypted_refresh_token, encrypted_refresh_token),
        machine_name = coalesce(nullif(p_machine_name, ''), machine_name),
        ip_address = p_ip_address,
        last_seen_at = now(),
        expires_at = lease_until
    where user_login_sessions.id = existing.id
    returning user_login_sessions.id,
              user_login_sessions.organization_id,
              user_login_sessions.user_id,
              user_login_sessions.device_id,
              user_login_sessions.last_seen_at,
              user_login_sessions.expires_at
    into id, organization_id, user_id, device_id, last_seen_at, expires_at;
  else
    insert into public.user_login_sessions (
      organization_id,
      user_id,
      device_id,
      machine_name,
      ip_address,
      token_hash,
      encrypted_refresh_token,
      expires_at)
    values (
      profile_row.organization_id,
      p_user_id,
      p_device_id,
      coalesce(p_machine_name, ''),
      p_ip_address,
      p_token_hash,
      p_encrypted_refresh_token,
      lease_until)
    returning user_login_sessions.id,
              user_login_sessions.organization_id,
              user_login_sessions.user_id,
              user_login_sessions.device_id,
              user_login_sessions.last_seen_at,
              user_login_sessions.expires_at
    into id, organization_id, user_id, device_id, last_seen_at, expires_at;
  end if;

  update public.profiles
  set last_login_at = now()
  where profiles.id = p_user_id;

  return next;
end;
$$;

create or replace function public.heartbeat_examtransfer_login_session(
  p_session_id uuid,
  p_user_id uuid,
  p_device_id text,
  p_lease_seconds integer default 120
)
returns table (
  id uuid,
  last_seen_at timestamptz,
  expires_at timestamptz
)
language plpgsql
security definer
set search_path = public
as $$
begin
  if auth.uid() is null or auth.uid() <> p_user_id then
    raise exception 'DEVICE_MISMATCH' using errcode = '28000';
  end if;

  update public.user_login_sessions
  set last_seen_at = now(),
      expires_at = now() + make_interval(secs => greatest(coalesce(p_lease_seconds, 120), 30))
  where user_login_sessions.id = p_session_id
    and user_login_sessions.user_id = p_user_id
    and user_login_sessions.device_id = p_device_id
    and user_login_sessions.revoked_at is null
    and user_login_sessions.expires_at > now()
  returning user_login_sessions.id,
            user_login_sessions.last_seen_at,
            user_login_sessions.expires_at
  into id, last_seen_at, expires_at;

  if id is null then
    raise exception 'LOGIN_SESSION_EXPIRED' using errcode = '28000';
  end if;

  return next;
end;
$$;

create or replace function public.release_examtransfer_login_session(
  p_session_id uuid,
  p_user_id uuid,
  p_device_id text,
  p_reason text default 'logout'
)
returns void
language plpgsql
security definer
set search_path = public
as $$
begin
  if auth.uid() is null or auth.uid() <> p_user_id then
    raise exception 'DEVICE_MISMATCH' using errcode = '28000';
  end if;

  update public.user_login_sessions
  set revoked_at = coalesce(revoked_at, now()),
      revoke_reason = coalesce(revoke_reason, nullif(p_reason, ''), 'logout'),
      encrypted_refresh_token = null
  where user_login_sessions.id = p_session_id
    and user_login_sessions.user_id = p_user_id
    and user_login_sessions.device_id = p_device_id;
end;
$$;

revoke all on function public.claim_examtransfer_login_session(uuid, text, text, inet, text, text, integer) from public;
revoke all on function public.heartbeat_examtransfer_login_session(uuid, uuid, text, integer) from public;
revoke all on function public.release_examtransfer_login_session(uuid, uuid, text, text) from public;
grant execute on function public.claim_examtransfer_login_session(uuid, text, text, inet, text, text, integer) to authenticated;
grant execute on function public.heartbeat_examtransfer_login_session(uuid, uuid, text, integer) to authenticated;
grant execute on function public.release_examtransfer_login_session(uuid, uuid, text, text) to authenticated;

update public.examtransfer_cloud_meta
set schema_version = 7,
    updated_at = now()
where id = 1;
