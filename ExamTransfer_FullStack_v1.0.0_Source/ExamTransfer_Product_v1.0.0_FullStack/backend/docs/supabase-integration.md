# Supabase production integration

## Operating model

ExamTransfer uses split authority. SQLite and local file storage are authoritative
for `LanOnly`; Supabase is authoritative for PublicCloud enrollment, participants,
devices, violations, submissions, and quiz attempts. A cloud outage never rolls
back or stops LAN exam operations.

## Access modes

### UserSession (default)

Use on teacher desktops and local servers distributed to end users. The app stores
only the project URL, publishable key and organization UUID. A Teacher/Admin signs
in through Supabase Auth. Metadata and Storage requests use the publishable key plus
the user access token, so PostgreSQL and Storage RLS remain the authorization
boundary.

### TrustedServer

Use only on a school-owned server controlled by administrators. Supply the Supabase
secret/service-role key through `EXAMTRANSFER_SUPABASE_SECRET_KEY`. The key is never
stored in the Frontend or runtime JSON. This credential bypasses RLS and must not be
used on student or generally distributed teacher machines.

## Canonical cloud source

`backend/supabase` is the only migration source. `database/supabase` may contain
only its legacy README and must not contain config, seed, functions, or migrations.

Migrations:

1. Initial core schema.
2. Full Frontend/Backend projection schema.
3. Tenant and RLS hardening.
4. Authenticated tenant bootstrap RPC.
5. UserSession Storage policies and audit insert policy.

All archive buckets are private. Object paths begin with:

```text
{organizationId}/{environment}/{entityType}/{entityId}/{fileName}
```

## Configure a development project

```powershell
.ackend\scripts\push-supabase-schema.ps1 -ProjectRef "PROJECT_REF"

.ackend\scripts\configure-supabase.ps1 `
  -SupabaseUrl "https://PROJECT_REF.supabase.co" `
  -PublishableKey "sb_publishable_..." `
  -OrganizationId "ORGANIZATION_UUID" `
  -AccessMode UserSession `
  -Environment Development
```

The non-secret machine configuration is written to:

```text
%ProgramData%\ExamTransfer\configuntime-settings.json
```

## Provision the first tenant

1. Create an Auth user in Supabase Dashboard or through the approved enrollment
   process.
2. Sign in once and obtain the user access token.
3. Run:

```powershell
.ackend\scriptsootstrap-supabase-tenant.ps1 `
  -SupabaseUrl "https://PROJECT_REF.supabase.co" `
  -PublishableKey "sb_publishable_..." `
  -AccessToken "USER_ACCESS_TOKEN" `
  -OrganizationName "Tên trường" `
  -DisplayName "Quản trị viên"
```

4. Put the returned UUID in `Cloud:OrganizationId`.
5. Disable public sign-up if organizations must be provisioned only by an
   administrator.

## Runtime validation

From the teacher app, open Settings → Supabase Cloud, save the URL, publishable key,
organization ID and `UserSession`, then sign in. Run **Kiểm tra Supabase**. A ready
configuration reports:

- configured = true;
- authenticated = true;
- canSynchronize = true;
- reachable = true.

For `TrustedServer`, set the secret only in the Backend process environment:

```powershell
$env:EXAMTRANSFER_SUPABASE_SECRET_KEY = "sb_secret_..."
```

## Upload strategy

Files up to 6 MiB use standard Storage upload. Larger files use TUS with 6 MiB chunks.
The upload URL and byte offset are checkpointed in SQLite so the worker can resume
after network loss or Backend restart.

## Cloud data

PostgreSQL stores organization-scoped metadata. Private Storage stores exam files,
submission files, graded attachments, exports and backups. Local-only runtime data
includes transfer chunks, heartbeats, device policy acknowledgements, local app
settings, logs and the outbox itself.

## Recovery

Cloud backups can be listed and downloaded through the Backend. Restore remains a
local scheduled operation and is blocked while an exam room is active. The Frontend
never receives Storage secret credentials.

## Auth production policy

Với hệ thống nội bộ trường học, tắt public sign-up trong Supabase Auth. Tài khoản Admin/Teacher được quản trị viên tạo trước, sau đó bootstrap organization/profile. Nếu triển khai mô hình SaaS tự đăng ký, phải bổ sung quy trình xác minh email, giới hạn tạo organization và kiểm soát abuse trước khi bật sign-up.

## Bootstrap tenant bằng tài khoản đầu tiên

Sau khi tạo một Auth user Admin trong Supabase Dashboard, có thể để script tự đăng nhập và tạo organization/profile:

```powershell
$securePassword = Read-Host "Supabase password" -AsSecureString
.\backend\scripts\bootstrap-supabase-tenant.ps1 `
  -SupabaseUrl "https://PROJECT_REF.supabase.co" `
  -PublishableKey "sb_publishable_..." `
  -Email "admin@school.edu" `
  -Password $securePassword `
  -OrganizationName "Tên trường"
```

UUID trả về là `OrganizationId` để cấu hình Backend/Frontend.
