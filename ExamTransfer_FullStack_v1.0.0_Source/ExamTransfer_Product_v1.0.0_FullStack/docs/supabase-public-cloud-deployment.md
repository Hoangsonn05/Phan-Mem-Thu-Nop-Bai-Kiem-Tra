# Supabase PublicCloud deployment

## Release gate

PublicCloud schema compatibility is `14`. LAN remains usable when cloud is
unconfigured, offline, or incompatible. Both cloud workers stop when the
capability RPC does not report schema 14, all critical RPCs, `exam-archives`,
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
2. Create and verify a Supabase database backup. Record its identifier and a
   tested restore procedure outside this repository.
3. Run the read-only report
   `backend/supabase/preflight/public_cloud_completion_v2.sql`. Resolve every
   duplicate PublicCloud idempotency/archive row and invalid legacy archive by
   an approved data process. The report never deletes data.
4. From `backend`, and only if this checkout is already linked to the intended
   staging project, run:

   ```powershell
   supabase migration list
   supabase db push --dry-run
   ```

   If the checkout is not linked, stop. Do not run `supabase link`, migration
   repair, or a real push merely to complete this checklist.

## Apply to staging

After review of the dry run, an authorized operator may apply migrations using
the team's normal release process. The completion migration is
`20260722161450_public_cloud_completion_v2.sql`; it is additive and corrects
the earlier global submission constraints with PublicCloud-only partial
indexes.

Authorized staging deployment commands (not run by this implementation turn):

```powershell
supabase migration list
supabase db push --dry-run
supabase db push
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
