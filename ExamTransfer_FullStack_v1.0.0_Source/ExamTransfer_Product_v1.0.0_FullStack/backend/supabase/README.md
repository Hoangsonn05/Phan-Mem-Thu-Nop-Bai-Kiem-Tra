# ExamTransfer Supabase schema

This is the single canonical Supabase source for the project. Do not create a second migration tree under `database/`.

## Deployment order

1. Create a Development Supabase project.
2. Run `backend/scripts/push-supabase-schema.ps1 -ProjectRef <ref> -RunDatabaseTests`.
3. Create an Auth user, sign in, then run `bootstrap-supabase-tenant.ps1`.
4. Configure the local server with `configure-supabase.ps1` or the Teacher Settings screen.
5. Use `UserSession` for distributed teacher computers. Use `TrustedServer` only on an administrator-controlled machine and supply its secret key through an environment variable.

Buckets are private. Metadata and object paths are organization-scoped. Local SQLite and local files remain authoritative while an exam is running.
