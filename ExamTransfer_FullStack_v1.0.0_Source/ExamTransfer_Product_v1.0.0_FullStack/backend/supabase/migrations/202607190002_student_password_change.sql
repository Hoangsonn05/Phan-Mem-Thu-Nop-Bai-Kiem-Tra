-- ExamTransfer Stage 6: đổi mật khẩu bắt buộc cho tài khoản Student.
-- Mật khẩu được thay đổi qua Supabase Auth. Hàm RPC này chỉ đánh dấu
-- hồ sơ của chính người dùng đã hoàn thành đổi mật khẩu.

begin;

create or replace function public.complete_own_password_change()
returns boolean
language plpgsql
security definer
set search_path = ''
as $function$
declare
  changed_rows integer;
begin
  if (select auth.uid()) is null then
    return false;
  end if;

  update public.profiles
  set
    must_change_password = false,
    updated_at = now()
  where id = (select auth.uid())
    and role = 'Student'
    and is_active = true;

  get diagnostics changed_rows = row_count;
  return changed_rows = 1;
end
$function$;

comment on function public.complete_own_password_change() is
  'Đánh dấu tài khoản Student hiện tại đã đổi mật khẩu; chỉ gọi sau khi Supabase Auth cập nhật mật khẩu thành công.';

revoke all on function public.complete_own_password_change() from public;
revoke all on function public.complete_own_password_change() from anon;
grant execute on function public.complete_own_password_change() to authenticated;

insert into public.examtransfer_cloud_meta(id, schema_version, updated_at)
values (1, 12, now())
on conflict (id) do update
set schema_version = excluded.schema_version,
    updated_at = excluded.updated_at;

commit;
