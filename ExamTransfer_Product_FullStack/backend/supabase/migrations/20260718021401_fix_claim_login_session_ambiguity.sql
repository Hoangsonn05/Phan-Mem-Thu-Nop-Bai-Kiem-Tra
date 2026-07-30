do $fix_claim_login_session$
declare
  v_function_oid oid;
  v_original_definition text;
  v_patched_definition text;
begin
  /*
    Chỉ chấp nhận đúng một function có tên này.
    Nếu không tồn tại hoặc có nhiều overload, migration phải dừng
    thay vì vá nhầm function.
  */
  select p.oid
  into strict v_function_oid
  from pg_proc as p
  inner join pg_namespace as n
    on n.oid = p.pronamespace
  where n.nspname = 'public'
    and p.proname = 'claim_examtransfer_login_session';

  v_original_definition :=
    pg_get_functiondef(v_function_oid);

  v_patched_definition := v_original_definition;

  /*
    Phân biệt rõ biến PL/pgSQL và cột của bảng đích.
  */
  v_patched_definition := replace(
    v_patched_definition,
    'revoke_reason = coalesce(revoke_reason, ''lease_expired'')',
    'revoke_reason = coalesce(user_login_sessions.revoke_reason, ''lease_expired'')'
  );

  v_patched_definition := replace(
    v_patched_definition,
    'and revoked_at is null',
    'and user_login_sessions.revoked_at is null'
  );

  v_patched_definition := replace(
    v_patched_definition,
    'and expires_at <= now()',
    'and user_login_sessions.expires_at <= now()'
  );

  if v_patched_definition = v_original_definition then
    raise exception
      'Không tìm thấy đoạn SQL cần vá trong function claim_examtransfer_login_session.';
  end if;

  /*
    pg_get_functiondef trả về dấu ; cuối câu lệnh.
    Loại bỏ dấu này trước khi chạy dynamic SQL.
  */
  v_patched_definition := regexp_replace(
    v_patched_definition,
    ';[[:space:]]*$',
    ''
  );

  execute v_patched_definition;
end;
$fix_claim_login_session$;