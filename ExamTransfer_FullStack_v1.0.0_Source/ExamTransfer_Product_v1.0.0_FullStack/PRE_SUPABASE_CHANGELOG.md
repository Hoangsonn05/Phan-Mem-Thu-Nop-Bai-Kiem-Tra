# Pre-Supabase Changelog

## Authentication and sessions

- Added account auth contracts: `AccountLoginRequest`, `AccountLoginResultDto`, `StudentIdentityConfirmRequest`, `CurrentAccountDto`, account heartbeat and logout DTOs.
- Added explicit auth error codes including `ACCOUNT_ALREADY_ACTIVE`, `SUPABASE_NOT_CONFIGURED`, `PARTICIPANT_TOKEN_REQUIRED`, and `PARTICIPANT_ACCOUNT_MISMATCH`.
- Added backend account auth services for Supabase password validation, student login challenges, account token signing, token hashing, and SQLite single-session leases.
- Split backend auth schemes into account tokens (`Authorization`) and participant tokens (`X-Exam-Session-Token`).
- Added `/api/v1/auth/login`, `/api/v1/auth/student/confirm`, `/api/v1/auth/me`, `/api/v1/auth/heartbeat`, and `/api/v1/auth/logout`.
- Disabled development teacher token by default with `Auth:AllowDevelopmentToken=false` and no built-in fallback token.
- Added backend auth/session tests for teacher login, student challenge/confirm, active-account conflicts, heartbeat/logout, inactive accounts, and token validation.
- Fixed SQLite `DateTimeOffset` ordering in account session claims by sorting active sessions in memory after the user-scoped query.

## Frontend

- Replaced manual Teacher/Student mode switching with a single login screen.
- Added DPAPI-protected account token restore and `/auth/me` validation on startup.
- Added account heartbeat and best-effort logout clearing local tokens.
- Updated `BackendClient` so account and participant tokens cannot overwrite each other.
- Updated student join to use authenticated account identity and store only the participant token in `StudentSessionState`.
- Removed the standalone student workspace page/viewmodel; workspace scanning now lives inside the current-exam screen.
- Moved workspace watching/scanning into `Kỳ thi hiện tại`.
- Changed mock mode default to `false`; mock mode now follows the new auth contract when explicitly enabled.

## Supabase

- Added forward-only migration `202607150007_application_auth_single_session.sql`.
- Added `public.user_login_sessions`, Student role support, profile account columns, single-session RPCs, RLS and grants.
- Added pgTAP coverage for the new auth schema surface.

## Tooling and docs

- Added `scripts/verify-pre-supabase.ps1`.
- Made `scripts/verify-pre-supabase.ps1` fail fast when native commands return a non-zero exit code.
- Included Supabase `db reset`, `db lint`, and `test db` in the verification script when Supabase checks are not skipped.
- Added `backend/docs/account-auth-flow.md`.
- Converted `frontend/FEATURE_CORE_CHECKLIST.md` into a feature-to-code matrix.
