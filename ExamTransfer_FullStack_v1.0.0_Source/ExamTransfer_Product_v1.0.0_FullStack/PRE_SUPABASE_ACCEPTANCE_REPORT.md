# Pre-Supabase Acceptance Report

Date: 2026-07-15

## Root causes fixed

- The app previously started with manual Teacher/Student mode selection, so the client could effectively choose its own role.
- Student room join was anonymous/client-identity driven and trusted `StudentCode` / `DisplayName` from the request.
- Account tokens and exam participant tokens shared the same bearer slot, so joining a room could overwrite the application login token.
- Development teacher token behavior existed as a fallback path; it is now disabled by default and has no built-in token value.
- Production startup now always uses the real backend client.
- There was no dedicated application account session service, challenge flow, heartbeat, logout, or single-session lease.
- Supabase schema did not yet model Student account login, profile fields, or atomic single-session RPCs.
- The standalone student workspace page duplicated functionality that belongs in the current exam flow.
- SQLite cannot translate `DateTimeOffset` ordering in account-session queries; the claim flow now sorts after a user-scoped fetch.

## Files added

- `backend/src/ExamTransfer.Shared.Contracts/AuthDtos.cs`
- `backend/src/ExamTransfer.Infrastructure/Security/AccountTokenService.cs`
- `backend/src/ExamTransfer.Infrastructure/Security/AccountAuthenticationService.cs`
- `backend/src/ExamTransfer.Infrastructure/Security/AccountSessionService.cs`
- `backend/src/ExamTransfer.Infrastructure/Security/LoginChallengeService.cs`
- `backend/src/ExamTransfer.Infrastructure/Security/SupabaseIdentityClient.cs`
- `backend/src/ExamTransfer.LocalServer/Controllers/AuthController.cs`
- `backend/supabase/migrations/202607150007_application_auth_single_session.sql`
- `backend/supabase/tests/0002_application_auth_single_session.sql`
- `backend/docs/account-auth-flow.md`
- `backend/tests/ExamTransfer.Infrastructure.Tests/ExamTransfer.Infrastructure.Tests.csproj`
- `backend/tests/ExamTransfer.Infrastructure.Tests/AccountAuthFlowTests.cs`
- `frontend/src/ExamTransfer.Desktop/Services/AppAuthSessionState.cs`
- `frontend/src/ExamTransfer.Desktop/ViewModels/LoginViewModel.cs`
- `frontend/src/ExamTransfer.Desktop/Views/LoginView.xaml`
- `frontend/src/ExamTransfer.Desktop/Views/LoginView.xaml.cs`
- `scripts/verify-pre-supabase.ps1`
- `PRE_SUPABASE_CHANGELOG.md`
- `PRE_SUPABASE_ACCEPTANCE_REPORT.md`

## Files modified

- Contracts: `Common.cs`, `Requests.cs`
- Backend project/test config: `backend/ExamTransfer.sln`, `backend/Directory.Packages.props`
- Backend application/domain/persistence/options/DI: `Abstractions.cs`, `Entities.cs`, `AppDbContext.cs`, `DbInitializer.cs`, `Options.cs`, `DependencyInjection.cs`
- Backend auth/session flow: `ExamTransferAuthHandler.cs`, `Program.cs`, `ApiControllerBase.cs`, `SessionsController.cs`, `ExamsController.cs`, `SubmissionsController.cs`, `ExamHub.cs`, `SessionTokenService.cs`, `SessionService.cs`, `AccountSessionService.cs`
- Backend config/docs/scripts: `appsettings.json`, `appsettings.Development.json`, `ExamTransfer.http`, `smoke-test-supabase.ps1`, `frontend-integration.md`
- Supabase tests: `backend/supabase/tests/0001_schema_and_rls.sql`
- Frontend shell/auth/client: `MainViewModel.cs`, `MainWindow.xaml`, `ServiceContracts.cs`, `BackendClient.cs`
- Frontend student flow: `StudentConnectViewModel.cs`, `StudentConnectView.xaml`, `StudentExamViewModel.cs`, `LiveMonitorViewModel.cs`, `ProductModules.cs`
- Frontend/docs/scripts: `FEATURE_CORE_CHECKLIST.md`, `README.md`, `frontend/README.md`, `frontend/docs/implementation-status.md`, `frontend/docs/screen-map.md`, `scripts/run-frontend.ps1`

## Files deleted

- `frontend/src/ExamTransfer.Desktop/Views/StudentWorkspaceView.xaml`
- `frontend/src/ExamTransfer.Desktop/Views/StudentWorkspaceView.xaml.cs`
- `StudentWorkspaceViewModel` was removed from `frontend/src/ExamTransfer.Desktop/ViewModels/ProductModules.cs`.

## New endpoints

- `POST /api/v1/auth/login`
- `POST /api/v1/auth/student/confirm`
- `GET /api/v1/auth/me`
- `POST /api/v1/auth/heartbeat`
- `POST /api/v1/auth/logout`

## Contract and auth changes

- `ContractInfo.SchemaVersion` is now `1.2.0`.
- Added `AccountLoginRequest`, `AccountLoginResultDto`, `StudentIdentityConfirmRequest`, `CurrentAccountDto`, `AccountHeartbeatRequest`, `AccountHeartbeatResponse`, and expanded `LogoutRequest`.
- Added error codes for invalid credentials, inactive accounts, student confirmation, identity mismatch, active-account conflict, expired sessions, device mismatch, provider/config failures, and participant/account mismatch.
- Account auth uses `Authorization: Bearer <account-token>`.
- Exam participant auth uses `X-Exam-Session-Token: <participant-token>`.
- Teacher/Admin policies use account auth; student room join requires authenticated Student account; room/exam/submission participant actions require participant auth.

## Supabase migration

- Added forward-only migration `backend/supabase/migrations/202607150007_application_auth_single_session.sql`.
- Adds Student role support, profile account fields, normalized unique indexes, `public.user_login_sessions`, RLS/grants, and RPCs:
  - `claim_examtransfer_login_session`
  - `heartbeat_examtransfer_login_session`
  - `release_examtransfer_login_session`
- Updated schema-version expectation to 7 and added pgTAP coverage in `0002_application_auth_single_session.sql`.

## Verification results

- `powershell -ExecutionPolicy Bypass -File scripts/verify-pre-supabase.ps1 -SkipSupabase`: PASS
- `dotnet restore backend/ExamTransfer.sln`: PASS, all projects up-to-date
- `dotnet build backend/ExamTransfer.sln -c Release --no-restore`: PASS, 0 warnings, 0 errors
- `dotnet test backend/ExamTransfer.sln -c Release --no-build`: PASS, 6 passed, 0 failed, 0 skipped
- `dotnet restore frontend/src/ExamTransfer.Desktop/ExamTransfer.Desktop.csproj`: PASS, all projects up-to-date
- `dotnet build frontend/src/ExamTransfer.Desktop/ExamTransfer.Desktop.csproj -c Release --no-restore`: PASS, 0 warnings, 0 errors
- Contract surface check in `scripts/verify-pre-supabase.ps1`: PASS
- `npx supabase db reset`: BLOCKED by local Docker Desktop/engine not available
- `npx supabase db lint --local --level warning`: BLOCKED by local Postgres connection failure
- `npx supabase test db`: BLOCKED by local Postgres connection failure

## Remaining real-environment steps

- Enter real Supabase URL and publishable key outside source.
- Link the Supabase project.
- Push/apply the new migration.
- Create real Admin/Teacher/Student test accounts and profiles.
- Run the full Supabase local or linked-remote test suite once Docker/local Postgres or a linked project is available.
- Run WPF runtime UI smoke testing on a desktop session; the current verification covered compile/build and contract checks only.

No real Supabase URL, publishable key, secret key, service-role key, or password was added to source. Existing `sb_publishable_...` and `sb_secret_...` strings are placeholders in docs/examples.
