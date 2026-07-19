# Account authentication and single-session policy

## Target flow

- There is no public registration endpoint.
- Teacher accounts are provisioned by an administrator in the database/Supabase Auth.
- Student accounts already exist; the student confirms account, student code and display name before entering.
- A successful login creates one row in `user_login_sessions`.
- Before creating a new row, the backend checks for an unrevoked, unexpired session for the same user. A different `device_id` is rejected with HTTP 409 (`ACCOUNT_ALREADY_ACTIVE`).
- Logout, administrator revocation or expiry sets `revoked_at_utc`; session rows are retained for audit.
- Only hashes of application session tokens are stored.

## Supabase mapping

- `users.supabase_auth_user_id` maps to `auth.users.id`.
- Domain roles remain in `public.users`; do not trust role metadata supplied by the client.
- `user_login_sessions` is server-managed. RLS denies direct client writes.
- Exam files and submissions use Supabase Storage; metadata and SHA-256 remain in PostgreSQL.
- Local SQLite remains a development/offline adapter behind `IAppDbContext`; production swaps to PostgreSQL without changing controllers/ViewModels.

## Required database constraints

- Unique: username, normalized email, student code, Supabase auth user ID, token hash.
- Partial unique index in PostgreSQL for one active session per user: `unique(user_id) where revoked_at_utc is null and expires_at_utc > now()` cannot use volatile `now()` directly; enforce using a transaction/advisory lock plus an index on `(user_id, revoked_at_utc)`.
- Foreign keys use UUID and UTC timestamps (`timestamptz`).
