# Account Auth Flow

ExamTransfer now separates application account authentication from exam participant authentication.

## Tokens

- Account token: sent as `Authorization: Bearer <account-token>`.
- Exam participant token: sent as `X-Exam-Session-Token: <participant-token>`.

The WPF client stores only the account token locally, protected with Windows DPAPI for the current user. Participant tokens live in `StudentSessionState` and are cleared when the student leaves the room or logs out.

## Endpoints

| Endpoint | Auth | Purpose |
|---|---|---|
| `POST /api/v1/auth/login` | Anonymous | Validates password with Supabase Auth and reads provisioned profile server-side. |
| `POST /api/v1/auth/student/confirm` | Anonymous plus challenge token | Confirms student code and display name after password validation. |
| `GET /api/v1/auth/me` | Account token | Returns `CurrentAccountDto`. |
| `POST /api/v1/auth/heartbeat` | Account token | Extends the single-session lease. |
| `POST /api/v1/auth/logout` | Account token | Idempotently revokes the login session. |
| `POST /api/v1/sessions/join` | Account token, `Student` role | Creates or resumes the participant using account identity. |

## Single Session

`AccountSessionService` claims a SQLite-backed lease in `user_login_sessions`. An active session from a different `device_id` returns HTTP 409 with `ACCOUNT_ALREADY_ACTIVE`. The same device can resume and extend the lease. Expired leases are revoked during the next claim.

The Supabase migration `202607150007_application_auth_single_session.sql` adds equivalent `user_login_sessions` storage and three RPCs:

- `claim_examtransfer_login_session`
- `heartbeat_examtransfer_login_session`
- `release_examtransfer_login_session`

## Supabase Readiness

`SupabaseIdentityClient` uses `HttpClientFactory` and reads only runtime configuration:

- `Cloud:SupabaseUrl`
- `Cloud:PublishableKey` or legacy `Cloud:AnonKey`

No Supabase URL, publishable key, secret key, service key, password, refresh token, or development account is hardcoded in source. Without runtime Supabase configuration, login returns `SUPABASE_NOT_CONFIGURED`.
