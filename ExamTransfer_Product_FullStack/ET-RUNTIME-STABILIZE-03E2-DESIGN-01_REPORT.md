# ET-RUNTIME-STABILIZE-03E2-DESIGN-01 Report

RESULT: PASS

TASK: ET-RUNTIME-STABILIZE-03E2-DESIGN-01

MODEL/CAPABILITY/REASONING: gpt-5.6-sol / C3-C4 / R3 High

HEAD: dc748e2cefa1b6c9990c2042060943c2e8f9c156

WORKTREE_BEFORE: Tracked clean; allowed untracked `ET-RUNTIME-REBASE-03A_REPORT.md` only.

WORKTREE_AFTER: Test/report changes only; existing untracked rebase report preserved; no stage/commit.

FILES_CREATED:

- `backend/tests/ExamTransfer.Infrastructure.Tests/RuntimeStabilize03E2DesignTests.cs`
- `ET-RUNTIME-STABILIZE-03E2-DESIGN-01_REPORT.md`

FILES_CHANGED:

- `frontend/tests/ExamTransfer.Desktop.Tests/TeacherRealtimeTests.cs`

PRODUCTION_FILES_CHANGED: NONE

PUBLICCLOUD_AUTHORITY: Supabase remains authoritative. The real adapter restricts pulls by `organization_id`, `source_mode=PublicCloud`, and ordered cloud cursor (`SupabaseCloudAdapter.cs:259-278`).

LOCAL_PROJECTION_ROLE: SQLite is a teacher-read projection. Pulled participant fields are stamped `SourceMode=PublicCloud`, `CloudVersion`, `CloudUpdatedAtUtc`, and `CloudSyncState=Pulled`.

TEACHER_READ_SOURCE: Local Server `GET api/v1/sessions/{id}` -> `SessionService.GetAsync` -> SQLite `ExamSession.Include(Participants)` (`SessionService.cs:67-71`). LiveMonitor does not read Supabase directly.

## Execution flow

| Time | Component/thread | Data source | Trigger | Expected order | Actual source/test evidence |
|---|---|---|---|---|---|
| T0 | Desktop student / async RPC | Supabase RPC | `JoinByRoomCodeAsync` | First | Calls `join_open_public_session_by_room_code` (`SupabasePublicCloudClient.cs:265-280`). |
| T1 | PostgreSQL transaction | `public.session_participants` | T0 | After T0 begins, before success returns | RPC inserts row at migration line 242 and returns its identifiers. Source is strong evidence; no live Supabase call ran. |
| T2 | Supabase Realtime | Private broadcast topic | Join mutation | Optional, before teacher refresh | MISSING for participant join. The join function contains no `realtime.send`; LiveMonitor does not construct/use `SupabaseRealtimeService`. |
| T3 | Desktop teacher UI | Local Server snapshot | Local SignalR notification/manual command | After a useful state change | Actual join path has no trigger. Injected early notification caused one stale refresh in deterministic frontend test. |
| T4 | Local Server background worker | Supabase REST pull | Host loop every 5 seconds | After T1 | Worker calls `PullAsync` with cursor (`PublicCloudPullWorker.cs:31-32, 110`). Deterministic pull test PASS. |
| T5 | Local Server worker transaction | SQLite | Pulled page | After T4 | `SaveChangesAsync` then `CommitAsync` (`PublicCloudPullWorker.cs:176-177`). Verified from a new DbContext. |
| T6 | Local Server realtime publisher | Local SignalR | Successful T5 | After commit | MISSING. Worker has no realtime dependency or publish call; registered recording publisher observed zero events. |
| T7 | LiveMonitor UI | Local Server session detail | Local SignalR or manual refresh | After T6 | `OnRealtimeNotification` schedules `LoadSessionAsync` (`LiveMonitorViewModel.cs:136-154`). No timer/poll exists. |
| T8 | LiveMonitor participant list | In-memory DTO collection | T7 completes | Last | Visible after a post-commit local signal or manual/unrelated refresh; not automatically after T5 today. |

T0_RPC_JOIN: STRONGLY_SUPPORTED. Client and migration contract align; not executed against staging/production.

T1_CLOUD_ROW: STRONGLY_SUPPORTED. RPC source performs the insert before return; live row creation was not tested.

T2_CLOUD_REALTIME: MISSING for participant join.

T3_EARLY_REFRESH: NOT_REPRODUCED in the actual join path. A deliberately injected early local notification reproduces the stale-refresh ordering condition.

T4_PULL_FETCH: PROVEN by deterministic fake-cloud pull.

T5_LOCAL_COMMIT: PROVEN by reopening SQLite in a separate DbContext.

T6_POST_PULL_PULSE: MISSING, PROVEN by source and recording publisher.

T7_TEACHER_RELOAD: EVENT_OR_MANUAL_ONLY; no polling.

T8_PARTICIPANT_VISIBLE: PROVEN after a post-commit local notification; stale without one.

## Pull and projection characterization

PULL_WORKER_FETCH_RESULT: PASS. New participant inserted from a cloud record.

CURSOR_VERSION_RESULT: PASS. Insert version 101, different-session version 102, update version 103, then stale version 102 ignored; entity cursor remains 103.

LOCAL_UPSERT_RESULT: PASS. SessionId, ParticipantId, student identity, `SourceMode=PublicCloud`, status, and CloudVersion verified. Existing participant accepted a newer version and rejected the stale payload.

LOCAL_COMMIT_RESULT: PASS. A new DbContext read the committed participant and cursor.

PARTICIPANT_DELETE_OR_TERMINAL_RESULT: Contract has no participant tombstone/soft-delete field. Terminal enum status is projected like other statuses; deletion characterization is NOT_APPLICABLE_CONTRACT_ABSENT.

REALTIME_BEFORE_PULL_RESULT: Actual PublicCloud join notification NOT_REPRODUCED because the producer/teacher subscription path is absent. Hypothetical early-notification ordering is PROVEN stale by deterministic test.

REFRESH_COUNT_BEFORE_PULL: 2 total snapshot reads: 1 initialization + 1 injected early notification.

REFRESH_COUNT_AFTER_PULL: Still 2 without pulse; becomes 3 only after an injected post-commit local pulse.

POST_PULL_LOCAL_PULSE: MISSING / PROVEN.

## Snapshot and ViewModel filtering

TEACHER_SNAPSHOT_RESULT: PASS. Snapshot contains the committed participant.

SESSION_FILTER_RESULT: PASS. Snapshot query selects the requested session and its navigation collection; a participant in another session is excluded.

ACCESS_MODE_FILTER_RESULT: NO PARTICIPANT FILTER. PublicCloud session access mode remains present in `SessionSummaryDto`; the participant is not hidden by AccessMode.

PARTICIPANT_STATUS_FILTER_RESULT: NO FILTER. PendingApproval and Approved participants are included by `ToDetail`; status only affects counts.

VIEWMODEL_FILTER_RESULT: PASS. LiveMonitor replaces its list with `detail.Participants`; notification handling rejects foreign session IDs and disposed instances. Three same-session OnlyLAN notifications debounce to one reload.

OTHER_FILTER_RESULT: No soft-delete, student account, device status, class membership, or participant-status predicate exists in the teacher `ToDetail` projection. Actual cloud organization/source-mode scoping occurs before projection in `SupabaseCloudAdapter`.

## Confidence

CLOUD_ROW_CREATED_CONFIDENCE: STRONGLY_SUPPORTED

PULL_WORKER_CONFIDENCE: PROVEN

LOCAL_PROJECTION_CONFIDENCE: PROVEN

REFRESH_RACE_CONFIDENCE: Actual cloud-realtime-before-pull path NOT_REPRODUCED; stale early-refresh condition PROVEN when such a notification is injected.

TEACHER_VISIBILITY_ROOT_CAUSE: PROVEN locally. SQLite receives the participant, and the teacher snapshot returns it, but `PublicCloudPullWorker` emits no post-commit session-scoped local signal while LiveMonitor has neither cloud subscription nor polling. The in-memory list therefore remains stale until another local notification or manual refresh. Live/staging latency remains UNKNOWN.

EXACT_VISIBILITY_ROOT_CAUSE: POST_PULL_LOCAL_PULSE_MISSING

## Candidate patch boundaries (not implemented)

OPTION_A_EVENT_PULSE:

- CORRECTNESS: Best fit. After a successful SQLite commit, publish a new local `PublicCloudProjectionUpdated` event scoped to each changed session. Payload minimum: `SessionId`, `EntityType`, `ProjectionVersion`/latest marker.
- ONLYLAN IMPACT: None if emitted only by `PublicCloudPullWorker`; existing OnlyLAN producer behavior remains unchanged. New test guards OnlyLAN debounce/dispose behavior.
- PUBLICCLOUD AUTHORITY: Preserved. Event only announces a committed projection; UI still reads the Local Server snapshot, and no frontend cloud write/read authority is added.
- DUPLICATE REFRESH RISK: Low with current 150 ms debouncer, but the ViewModel should explicitly accept/dedupe by session and projection marker.
- FAILURE RECOVERY: A transient SignalR publish failure or disconnected client can lose a one-shot pulse. R1 planning must include bounded retry/catch-up on reconnect or a durable pending projection marker; do not claim fire-and-forget alone is fully reliable.
- COMPLEXITY: Low to medium; shared event contract, worker post-commit collection/publish, Lobby/LiveMonitor handling, and characterization tests.
- RECOMMENDATION: RECOMMENDED, with post-commit ordering and bounded recovery designed before implementation.

OPTION_B_BOUNDED_POLL:

- CORRECTNESS: Can recover lost events if a session-scoped projection revision endpoint exists.
- ONLYLAN IMPACT: Must be enabled only for `SessionAccessMode.PublicCloud`.
- PUBLICCLOUD AUTHORITY: Preserved if polling reads Local Server projection revision, not cloud rows.
- DUPLICATE REFRESH RISK: Medium; must reload only when revision advances.
- FAILURE RECOVERY: Better than one-shot event, but current system has no session-scoped projection revision endpoint.
- COMPLEXITY: Medium/high because a version contract and bounded lifecycle are required.
- RECOMMENDATION: Fallback/recovery only; do not introduce unbounded polling.

OPTION_C_DIRECT_CLOUD_READ:

- CORRECTNESS: Creates dual read authorities and reconciliation problems.
- ONLYLAN IMPACT: Requires new routing and increases divergence risk.
- PUBLICCLOUD AUTHORITY: Cloud authority is direct, but it bypasses the established local teacher projection boundary.
- DUPLICATE REFRESH RISK: High across cloud and local signals.
- FAILURE RECOVERY: Network/RLS failures directly affect teacher visibility.
- COMPLEXITY: High.
- RECOMMENDATION: REJECT for 03E2-R1.

RECOMMENDED_PATCH_BOUNDARY: Option A at the worker's post-commit boundary, plus bounded lost-signal recovery. Emit per changed PublicCloud session only; add no cloud fallback, no system-wide refresh, no OnlyLAN dependency.

ONLYLAN_IMPACT: NONE in design; OnlyLAN characterization remains PASS.

PUBLICCLOUD_AUTHORITY_IMPACT: NONE. Cloud stays authoritative; SQLite stays projection; UI reload occurs only after projection commit.

RPC_SCHEMA_IMPACT: NONE required for the recommended R1 boundary.

## Verification

FOCUSED_TEST_RESULTS:

- Backend final: 2/2 PASS (`RuntimeStabilize03E2DesignTests`). First run had one test-only path-resolution failure; the repo-root diagnostic helper was corrected, and the second/final run passed.
- Frontend: 2/2 PASS (new LiveMonitor characterization tests).

FULL_BACKEND_RESULTS: PASS, 249 passed / 1 existing skipped / 0 failed.

FULL_FRONTEND_RESULTS: PASS, 208 passed / 0 skipped / 0 failed.

AGGREGATE_RESULTS: PASS. OnlyLAN build, targeted characterization, login/auth freeze, PublicCloud authority regression, and full Infrastructure regression passed. Full-solution build SKIP (`not_requested`) by script contract.

DIFF_CHECK: PASS.

STAGING_AVAILABLE: NO; not configured or invoked in this task.

STAGING_ONLY_UNKNOWN:

- Real Supabase RPC-to-pull visibility latency and ordering.
- Live SignalR disconnect/reconnect and publish-failure recovery.
- Multi-machine teacher/student UI acceptance.

OUT_OF_SCOPE_FINDINGS: NONE requiring an adjacent production change. Supabase's current changelog notes Data API exposure changes for newly created tables/projects; no evidence links that change to this existing runtime path, and no schema/config action was taken.

REMAINING_RISKS:

- A one-shot local pulse without bounded/durable recovery can still be lost.
- Live/staging timing remains unverified.
- The global entity cursor is proven for tested ordered versions; production paging/latency remains covered only by existing regressions and source review.

FINAL_VERDICT: PASS_DESIGN_CHECKPOINT. Exact local visibility cause is proven; the production defect is not patched.

PATCH_03E2_R1_STATUS: AUTHORIZED_FOR_PLANNING

NEXT_TASK: `ET-RUNTIME-STABILIZE-03E2-R1-PLAN`; capability C3, model gpt-5.6-sol or equivalent flagship code model, reasoning R2-R3. Stop for explicit authorization before any production implementation.
