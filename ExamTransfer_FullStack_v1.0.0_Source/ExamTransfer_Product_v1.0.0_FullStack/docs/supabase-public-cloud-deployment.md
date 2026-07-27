# Supabase PublicCloud deployment

## Release gate

PublicCloud schema compatibility is `19`. LAN remains usable when cloud is
unconfigured, offline, or incompatible. Both cloud workers stop when the
capability RPC does not report schema 15, all critical RPCs, `exam-archives`,
and `public-submission-archives`.

Do not label a build production-ready until the staging roundtrip scripts have
passed against a real, isolated staging project and a restorable database
backup has been verified.

## Ownership contract

| Owner | Entities | Direction |
| --- | --- | --- |
| Local/teacher | classes, exams, exam files, public assignments, LAN/Public sessions and session state, policies, grades/publications | SQLite outbox to Supabase, optimistic `cloud_version` |
| Supabase | enrollment requests and enrollment-created members, PublicCloud participants, devices, violations, commands/results, submissions/files, quiz attempts/answers | Supabase cursor pull to SQLite; never reverse-outbox |

Realtime is notification-only. After reconnect, clients/workers must fetch an
authoritative snapshot. Private topics are separate:

- `exam-session:{sessionId}`: staff sends; members receive.
- `exam-session:{sessionId}:device:{deviceId}`: staff sends device commands.
- `exam-session:{sessionId}:telemetry:{deviceId}`: device owner sends; staff receives.

In the Supabase dashboard enable `Realtime -> Settings -> Channel Restrictions
-> Private channels only` before staging acceptance.

## Pre-deployment

1. Record `git status --short` and preserve the dirty worktree. Do not reset,
   restore, clean, stash, commit, or push as part of deployment validation.
2. Disable cloud and stop the production Local Server.
3. Confirm the exact linked Project Ref.
4. Run the read-only report
   `backend/supabase/preflight/public_cloud_production_legacy_preflight.sql`.
   Resolve every `BLOCKER` by an approved data process. The report never
   creates, updates, or deletes database objects/data.
5. From `backend`, and only if this checkout is already linked to the intended
   staging project, run:

   ```powershell
   supabase migration list
   supabase db push --dry-run
   ```

   If the checkout is not linked, stop. Do not run `supabase link`, migration
   repair, or a real push merely to complete this checklist.
6. Create both database and Storage backups with
   `backend/scripts/backup-supabase-production-all.ps1`, then run
   `verify-supabase-production-backup.ps1`. Do not apply migration unless it
   returns `BACKUP_READY`.

## Apply to staging

After review of the dry run and a verified backup, an authorized operator may
apply migrations using the team's normal release process. Completion migration
`20260722161450_public_cloud_completion_v2.sql` reaches schema 14 and corrects
global submission constraints with PublicCloud-only partial indexes.
`20260723043859_public_cloud_teacher_mutations_and_projection.sql` reaches
schema 15 with server-authorized, idempotent teacher mutations and projection
fields.
`20260727122721_session_first_open_request.sql` reaches schema 19 with
classless OpenRequest admission, atomic room-code joining, and guarded
manifest/download access.

Authorized staging deployment commands (not run by this implementation turn):

```powershell
supabase migration list
supabase db push --dry-run
# Không push trực tiếp. Dùng backend/scripts/apply-supabase-production-update.ps1 sau khi backup/readiness đạt.
supabase db lint --linked --level warning
supabase functions deploy verify-public-submission-archive
supabase functions deploy issue-public-device-command
supabase functions deploy get-public-exam-file-url
supabase secrets set EXAMTRANSFER_DEVICE_COMMAND_HMAC_SECRET="..."
```

Configure Edge secrets without writing values to logs or source:

- `SUPABASE_URL`
- `SUPABASE_ANON_KEY`
- `SUPABASE_SERVICE_ROLE_KEY`
- `EXAMTRANSFER_DEVICE_COMMAND_HMAC_SECRET` (at least 32 bytes)

Deploy:

- `verify-public-submission-archive`
- `get-public-exam-file-url`
- `issue-public-device-command`

The archive verifier must call `verify_public_submission_archive`; it must not
PATCH `submission_files`. Public submission objects use the immutable path
`org/public-submissions/user/submission/file-id.extension`, are limited to
10 MiB, and accept only ZIP/RAR/7Z metadata plus server-verified magic/SHA.
Signed exam URLs expire after 180 seconds.

Desktop PublicCloud configuration uses:

- `EXAMTRANSFER_SUPABASE_URL`
- `EXAMTRANSFER_SUPABASE_PUBLISHABLE_KEY`
- optional `EXAMTRANSFER_STUDENT_EMAIL_DOMAIN`

Never place a service-role or secret key on a student/teacher desktop.

## Migration audit

| Migration | Schema | Main changes | Legacy risk/dependency | Remote status |
| --- | ---: | --- | --- | --- |
| `20260722141147` | 13 | Public classes, sessions, devices, submissions, Storage/RLS/RPC | Must create partial PublicCloud indexes; depends on earlier profile/auth schema | Determine with `migration list` |
| `20260722161450` | 14 | Ownership completion, archive validation, private Realtime, capability RPC | Forward-fixes global indexes; trigger ignores Lan rows | Determine with `migration list` |
| `20260723043859` | 15 | Teacher mutation RPCs/idempotency and projection completion | Depends on schema 14 and existing PublicCloud ownership columns | Determine with `migration list` |
| `20260727122721` | 19 | OpenRequest admission and room-first PublicCloud joining | Forward-only; requires all schema 18 migrations | Determine with `migration list` |

If `20260722141147` is pending remotely, use the corrected source migration.
If it is already applied, do not rewrite remote history or use migration
repair; let `20260722161450` perform the safe forward fix.

## Staging verification

Run the source gates first:

```powershell
./backend/scripts/verify.ps1
./frontend/scripts/verify-frontend.ps1
```

Then run every live script with real staging credentials and identifiers:

```powershell
./backend/scripts/test-cloud-schema-version.ps1 ...
./backend/scripts/test-public-cloud-sync-roundtrip.ps1 ...
./backend/scripts/test-public-storage-workflow.ps1 ...
./backend/scripts/test-public-device-command.ps1 ...
./backend/scripts/test-public-quiz-workflow.ps1 ...
./backend/scripts/test-public-cloud-staging.ps1 ...
```

Each script exits non-zero when parameters, credentials, expected rows, or
server behavior are missing. Static/local fixture checks never count as a live
staging pass.

Verify at minimum: tenant isolation and direct-write denial, enrollment and
membership, local session push, cloud participant/submission/device/quiz pull,
restart-safe cursors, immutable upload and archive verification, short signed
download plus SHA, command signature/replay handling, realtime private-topic
authorization, and snapshot recovery after reconnect.

## Rollback and recovery

1. Stop PublicCloud workers by disabling cloud in runtime settings. LAN remains
   operational on SQLite.
2. Do not manually rewrite migration history. Restore the verified staging
   backup or apply a separately reviewed forward-fix migration.
3. Preserve `public_cloud_pull_failures` and cursor/replica tables for incident
   analysis. Do not delete quarantined rows automatically.
4. Rotate service-role/HMAC secrets if they may have been exposed.
5. Re-run the full staging gate before re-enabling workers.


## Production write guard

`backend/scripts/push-supabase-schema.ps1` is intentionally disabled for remote
writes. Production updates must use:

1. `check-production-update-readiness.ps1` with all local/Docker/Supabase gates.
2. `backup-supabase-production-all.ps1`.
3. `verify-supabase-production-backup.ps1`.
4. A second full readiness run with remote preflight/dry-run and the verified
   backup.
5. `apply-supabase-production-update.ps1` with the exact Project Ref,
   `BACKUP_VERIFIED_READY_FOR_PRODUCTION_UPDATE` JSON report, and explicit
   production-write confirmation.
