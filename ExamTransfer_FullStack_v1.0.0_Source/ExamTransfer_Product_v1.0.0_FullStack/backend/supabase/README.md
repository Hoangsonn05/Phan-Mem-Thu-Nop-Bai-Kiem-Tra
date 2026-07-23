# ExamTransfer Supabase schema

This is the single canonical Supabase source for the project. `database/supabase` is documentation-only and must never contain a second config, seed, or migration tree.

## Deployment order

1. Create a Development Supabase project.
2. Run `backend/scripts/push-supabase-schema.ps1 -ProjectRef <ref> -RunDatabaseTests`.
3. Create an Auth user, sign in, then run `bootstrap-supabase-tenant.ps1`.
4. Configure the local server with `configure-supabase.ps1` or the Teacher Settings screen.
5. Use `UserSession` for distributed teacher computers. Use `TrustedServer` only on an administrator-controlled machine and supply its secret key through an environment variable.

## PublicCloud security boundary

- Student table writes are denied for participants, submissions/files, device connections/results, violations, and quiz attempts/answers. Authenticated Students use only the `join_public_session`, `init_public_submission`, `finalize_public_submission`, `upsert_public_device_heartbeat`, `report_public_violation`, `ack_public_device_command`, `start_public_quiz_attempt`, `save_public_quiz_answers`, and `finalize_public_quiz_attempt` RPCs.
- `SECURITY DEFINER` RPCs have an empty `search_path`, derive user and organization from `auth.uid()`, require an active Student profile and class membership, and generate server-owned state themselves.
- Public submissions use the private `public-submission-archives` bucket and the exact path `organization-id/public-submissions/user-id/submission-id/file-id.extension`. The bucket is insert-only for Students and capped at 10 MiB. `functions/verify-public-submission-archive` verifies size, ZIP/RAR/7Z magic, and SHA-256, then calls the service-role-only transactional RPC; it never PATCHes metadata directly.
- Teacher device commands go through `functions/issue-public-device-command`. Only that Edge Function holds the HMAC and service-role secrets. The desktop client must never sign or insert `public_device_commands` directly.
- All Realtime clients must subscribe with `config: { private: true }`. Session, device command, and device telemetry topics are separate. In Dashboard > Realtime > Settings, enable **Private Channels Only**; this dashboard setting is intentionally not represented by a SQL migration.

## Authority and conflict rules

- LAN sessions: SQLite/local files are authoritative and the existing outbox pushes their projection.
- PublicCloud sessions and teacher-controlled state remain local-owned and are pushed with optimistic `cloud_version`; Supabase is authoritative only for enrollment-created members, participants, devices, violations, public submissions, command results, and quiz attempts/answers. Cloud-owned rows are never reverse-upserted.
- PublicCloud rows carry `source_mode='PublicCloud'` and a monotonic `cloud_version`. Pull consumers checkpoint `(last_cloud_version,last_updated_at,last_id)` per entity; re-reading a page is safe and must not be resolved by uncontrolled last-write-wins.

Static acceptance scripts report `STATIC_*` result codes. Run the `test-public-*.ps1` staging scripts with real staging JWTs and identifiers for live checks. No acceptance script links, pushes migrations, deploys functions, or prints supplied credentials.
