-- ExamTransfer: chuẩn hóa hồ sơ tài khoản học sinh trên Supabase.
-- Không lưu quê quán. Không lưu mật khẩu trong public.profiles.
-- Mật khẩu tiếp tục do Supabase Auth quản lý.

begin;

-- 1. Bổ sung dữ liệu hồ sơ học sinh.
alter table public.profiles
  add column if not exists date_of_birth date,
  add column if not exists must_change_password boolean not null default false;

comment on column public.profiles.date_of_birth is
  'Ngày sinh của người dùng; bắt buộc đối với hồ sơ Student.';
comment on column public.profiles.must_change_password is
  'True khi tài khoản đang dùng mật khẩu tạm và phải đổi mật khẩu.';

-- 2. Chuẩn hóa khoảng trắng của dữ liệu hiện có.
update public.profiles
set
  username = nullif(btrim(username), ''),
  student_code = nullif(btrim(student_code), ''),
  display_name = btrim(display_name),
  updated_at = now()
where
  username is distinct from nullif(btrim(username), '')
  or student_code is distinct from nullif(btrim(student_code), '')
  or display_name is distinct from btrim(display_name);

-- 3. Ràng buộc ngày sinh hợp lệ ở mức cơ bản.
do $migration$
begin
  if not exists (
    select 1
    from pg_constraint
    where conname = 'profiles_date_of_birth_reasonable_check'
      and conrelid = 'public.profiles'::regclass
  ) then
    alter table public.profiles
      add constraint profiles_date_of_birth_reasonable_check
      check (
        date_of_birth is null
        or date_of_birth >= date '1900-01-01'
      ) not valid;
  end if;
end
$migration$;

alter table public.profiles
  validate constraint profiles_date_of_birth_reasonable_check;

-- 4. Với role Student, các trường nhận dạng phải đầy đủ.
-- Dùng NOT VALID để không làm hỏng triển khai nếu đang có hồ sơ Student thử nghiệm cũ.
-- Mọi bản ghi Student tạo mới hoặc cập nhật sau migration vẫn bị kiểm tra ngay.
do $migration$
begin
  if not exists (
    select 1
    from pg_constraint
    where conname = 'profiles_student_required_fields_check'
      and conrelid = 'public.profiles'::regclass
  ) then
    alter table public.profiles
      add constraint profiles_student_required_fields_check
      check (
        role <> 'Student'
        or (
          username is not null
          and btrim(username) <> ''
          and student_code is not null
          and btrim(student_code) <> ''
          and display_name is not null
          and btrim(display_name) <> ''
          and date_of_birth is not null
          and lower(btrim(username)) = lower(btrim(student_code))
        )
      ) not valid;
  end if;
end
$migration$;

-- Nếu không còn hồ sơ Student cũ bị thiếu dữ liệu thì xác nhận constraint ngay.
do $migration$
begin
  if not exists (
    select 1
    from public.profiles
    where role = 'Student'
      and (
        username is null
        or btrim(username) = ''
        or student_code is null
        or btrim(student_code) = ''
        or display_name is null
        or btrim(display_name) = ''
        or date_of_birth is null
        or lower(btrim(username)) <> lower(btrim(student_code))
      )
  ) then
    alter table public.profiles
      validate constraint profiles_student_required_fields_check;
  end if;
end
$migration$;

-- 5. Chuẩn hóa RLS cho profiles:
-- Student chỉ đọc hồ sơ của chính mình.
-- Admin/Teacher đọc hồ sơ trong cùng organization.
alter table public.profiles enable row level security;

drop policy if exists "profile_select_scope" on public.profiles;
drop policy if exists "profiles_select_student_self_or_org_staff" on public.profiles;

create policy "profiles_select_student_self_or_org_staff"
on public.profiles
for select
to authenticated
using (
  (select auth.uid()) is not null
  and (
    id = (select auth.uid())
    or (
      organization_id = (select public.current_organization_id())
      and (select public.current_examtransfer_role()) in ('Admin', 'Teacher')
    )
  )
);

-- Hồ sơ là dữ liệu quản trị. Client đăng nhập chỉ được đọc.
-- Importer/backend dùng secret key phía server để tạo hoặc cập nhật profile.
drop policy if exists "profile_update_self" on public.profiles;
revoke all on public.profiles from anon;
revoke insert, update, delete on public.profiles from authenticated;
grant select on public.profiles to authenticated;

-- 6. Đánh dấu phiên bản cloud schema mới.
insert into public.examtransfer_cloud_meta(id, schema_version, updated_at)
values (1, 11, now())
on conflict (id) do update
set schema_version = excluded.schema_version,
    updated_at = excluded.updated_at;

commit;
