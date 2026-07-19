# ExamTransfer Backend v1.2.0

ASP.NET Core/.NET 10 local-first backend for ExamTransfer.

## Runtime responsibilities

- SQLite and local file storage for active LAN exams.
- REST API, SignalR and UDP discovery.
- Durable outbox for Supabase PostgreSQL/Storage.
- TUS resumable upload for cloud archives over 6 MiB.
- Tenant isolation with organization ID and Supabase RLS.
- Encrypted Supabase Auth session cache with proactive refresh.
- Cloud backup catalog, download, validation and restore.

## Build

```powershell
.\scripts\verify.ps1
```

## Supabase source

Canonical files:

```text
supabase/
├── config.toml
├── migrations/
├── seed.sql
└── tests/
```

Validate source layout:

```powershell
.\scripts\verify-supabase-source.ps1
```

Push linked schema:

```powershell
.\scripts\push-supabase-schema.ps1 -ProjectRef "PROJECT_REF" -RunDatabaseTests
```

Configure non-secret values and the trusted Backend secret:

```powershell
.\scripts\configure-supabase.ps1 `
    -SupabaseUrl "https://PROJECT_REF.supabase.co" `
    -PublishableKey "sb_publishable_..." `
    -OrganizationId "ORGANIZATION_UUID" `
    -Environment Production `
    -SecretKey "sb_secret_..."
```

See `docs/supabase-integration.md` for the full deployment and smoke-test workflow.
