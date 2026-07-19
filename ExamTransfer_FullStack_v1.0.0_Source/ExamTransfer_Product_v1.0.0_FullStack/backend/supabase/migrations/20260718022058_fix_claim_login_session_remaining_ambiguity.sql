do $fix_claim_login_session$
declare
  v_function_oid oid;
  v_original_definition text;
  v_patched_definition text;
begin
  /*
    Chỉ cho phép đúng một function có tên này.
    Nếu có nhiều overload hoặc function không tồn tại thì dừng migration.
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

  v_patched_definition :=
    v_original_definition;

  /*
    Sửa truy vấn tìm session đang còn hiệu lực.
  */
  v_patched_definition := replace(
    v_patched_definition,
    'and expires_at > now()',
    'and user_login_sessions.expires_at > now()'
  );

  /*
    last_seen_at cũng có khả năng bị xem là biến PL/pgSQL,
    nên định danh luôn để không phát sinh lỗi lint tiếp theo.
  */
  v_patched_definition := replace(
    v_patched_definition,
    'order by last_seen_at desc',
    'order by user_login_sessions.last_seen_at desc'
  );

  if v_patched_definition = v_original_definition then
    raise exception
      'Không tìm thấy đoạn SQL cần sửa trong claim_examtransfer_login_session.';
  end if;

  /*
    Loại bỏ dấu chấm phẩy cuối trước khi thực thi dynamic SQL.
  */
  v_patched_definition := regexp_replace(
    v_patched_definition,
    ';[[:space:]]*$',
    ''
  );

  execute v_patched_definition;
end;
$fix_claim_login_session$;