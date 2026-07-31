# ET-RUNTIME-FINAL-INTEGRATION-04A REPORT

RESULT: `FINAL_INTEGRATION_NO_GO`

TASK: `ET-RUNTIME-FINAL-INTEGRATION-04A`

MODEL/CAPABILITY/REASONING: Codex trên GPT-5; capability tương đương C4; R3 High

## Repository identity và worktree

HEAD: `724f34da4554c8af812b897fdb55e5f3304ad315`

INTEGRATION_BASE: `dd88ad3eabb7bf5a974c9a4b7a2f3e294e5f40f7`

INTEGRATION_RANGE: `dd88ad3eabb7bf5a974c9a4b7a2f3e294e5f40f7..724f34da4554c8af812b897fdb55e5f3304ad315`

WORKTREE_BEFORE: tracked clean; chỉ có ba báo cáo untracked được whitelist:

- `ET-RUNTIME-REBASE-03A_REPORT.md`
- `ET-RUNTIME-STABILIZE-03E2-DESIGN-01_REPORT.md`
- `ET-RUNTIME-STABILIZE-03E2-R1_REPORT.md`

WORKTREE_AFTER: tracked clean; thêm duy nhất báo cáo untracked được phép `ET-RUNTIME-FINAL-INTEGRATION-04A_REPORT.md`.

Start gate:

- `git rev-parse --show-toplevel`: `D:/MMO/PhanMemNopThuBaiKiemTra`
- `git diff --check`: PASS
- tracked worktree clean: PASS
- integration base resolved from the parent of the exact first-patch subject: PASS

## Patch checkpoints

| TASK | COMMIT | FILES | STATUS |
|---|---|---:|---|
| 03C | `d673fcaa766e7ab58e4a4e4c15487b3e0057d6f5` | 12 frontend source/test files | PASS |
| 03D authority | `e03a9708896c5989e17ffe360f861f9a1c2d0b4d` | migration plus backend/frontend contract tests | PASS |
| 03D authority wiring | `17b9330376eda92c3c5278ab4a7169efd42ffa05` | DTO, mapper, parser/state/view-model and pgTAP | PASS |
| 03D | `7f186d90302118c1d418a0929dc82858a7a4977a` | queue, eligibility policy, view-model and tests | PASS |
| 03E1 | `dc748e2cefa1b6c9990c2042060943c2e8f9c156` | submission policy/service/contracts/tests | PASS |
| 03E2 design characterization | `44b2e5ae75d9424227176cf891c6e6fcb525af9b` | backend/frontend tests only | PASS |
| 03E2 | `724f34da4554c8af812b897fdb55e5f3304ad315` | pull worker, event contract, SignalR, coordinator, monitor and tests | PASS |

PATCH_03C_COMMIT: `d673fcaa766e7ab58e4a4e4c15487b3e0057d6f5`

PATCH_03D_AUTHORITY_COMMITS: `e03a9708896c5989e17ffe360f861f9a1c2d0b4d`, `17b9330376eda92c3c5278ab4a7169efd42ffa05`

PATCH_03D_COMMIT: `7f186d90302118c1d418a0929dc82858a7a4977a`

PATCH_03E1_COMMIT: `dc748e2cefa1b6c9990c2042060943c2e8f9c156`

PATCH_03E2_COMMIT: `724f34da4554c8af812b897fdb55e5f3304ad315`

INTEGRATED_FILES: 35 files, 4,425 insertions and 236 deletions. Scope is limited to:

- backend contracts/mapping: `Common.cs`, `Dtos.cs`, `Events.cs`, `MappingExtensions.cs`;
- backend submission/projection: `SubmissionService.cs`, `SubmissionStatePolicy.cs`, `PublicCloudPullWorker.cs`;
- Supabase: new migration `20260731113609_public_student_timeline_resubmit_allowed.sql` and update to `0009_session_first_open_request.sql`;
- frontend lifecycle/submission/projection: `RealtimeService.cs`, `SubmissionQueueStore.cs`, `SupabasePublicCloudClient.cs`, `ProjectionRefreshCoordinator.cs`, `ServiceContracts.cs`, `StudentExamFlowCoordinator.cs`, `StudentSessionState.cs`, `SubmissionEligibilityPolicy.cs`, `LiveMonitorViewModel.cs`, `MainViewModel.cs`, `ProductModules.cs`, `StudentConnectViewModel.cs`, `StudentExamViewModel.cs`;
- directly related backend/frontend regression tests.

UNEXPECTED_FILES: none. Commit `44b2e5a` is test-only 03E2 characterization and contains no unexpected production change. No script changed in the integration range.

OLD_BYPASS_PATHS:

- `PollQueueAsync`: no production match remains; progress is projected from `SubmissionProgressSnapshot` events.
- `PrepareAsync`: compatibility wrapper remains but delegates to `PrepareOrGetActiveAsync`; no production caller bypasses single-flight.
- only one production `PrepareOrGetActiveAsync` call exists, in `StudentSubmissionViewModel`.
- no old join catch reports a committed join as mutation failure.
- no terminal timer resurrection path was found.
- `Task.Delay`, timers and loops were classified as bounded retry/countdown/background-worker paths. The existing backend PublicCloud pull loop is not a new frontend permanent polling fallback.

## Integrated architecture review

LIFECYCLE_03C_RESULT: PASS

- join mutation is mode-dispatched, then `ApplyJoin`, post-join synchronization and lifecycle resolution;
- a committed mutation is retained for post-join retry and is not repeated;
- LAN and PublicCloud use the same lifecycle classifier;
- `Collecting` stays in active/summary states instead of returning to waiting;
- Finished/Cancelled/Archived stop the timer;
- navigation and notifications are revision/session deduplicated;
- stale/equal active snapshots cannot overwrite a terminal state.

RESUBMIT_AUTHORITY_RESULT: PASS

- OnlyLAN: `SessionParticipant.ResubmitAllowed -> ParticipantDto -> MappingExtensions -> StudentSessionState`;
- PublicCloud: `session_participants.resubmit_allowed -> get_public_student_timeline -> System.Text.Json parser -> StudentSessionState`;
- missing JSON field defaults to false; a wrong JSON type throws rather than coercing;
- stale revisions are rejected before applying the authority;
- eligibility consumes `ResubmitAllowed` directly and does not infer it from `SubmissionStatus`.

FRONTEND_SUBMISSION_03D_RESULT: PASS

- the same `SubmissionEligibilityPolicy` is used by `CanExecute` and `SubmitAsync`;
- active persistent queue blocks a new attempt;
- business-key serialization plus `PrepareOrGetActiveAsync` returns the existing queue without creating a new queue/key/spool;
- a new attempt after a terminal result requires `ResubmitAllowed=true`;
- upload/finalize/receipt authority remains in `SubmissionRecoveryService`;
- progress reaches 100 only with a confirmed receipt.

BACKEND_SUBMISSION_03E1_RESULT: PASS

- Init/Chunk/Finalize share `SubmissionStatePolicy.SessionAcceptsSubmission`, allowing only InProgress/Collecting;
- unique `(ParticipantId, AttemptNumber)` and `(ParticipantId, IdempotencyKey)` indexes provide the database invariant;
- same-key concurrent init is idempotent; different-key contention returns typed conflict;
- SQLite uniqueness errors are translated, not leaked;
- terminal chunk/finalize is rejected, while retry of an already finalized valid receipt remains idempotent;
- resubmit authority is consumed atomically when the next attempt is created;
- no static process lock is the sole invariant.

PUBLICCLOUD_REFRESH_03E2_RESULT: PASS

- projection versions are collected only for changed PublicCloud participant rows;
- event publication happens only after projection/cursor transaction commit;
- events are coalesced per session using max version and contain only SessionId, EntityType and ProjectionVersion;
- same/lower versions are ignored; frontend refresh is serialized with bounded retries/recovery;
- OnlyLAN rows neither emit nor consume this projection event.

## Cross-mode authority matrix

ONLYLAN_AUTHORITY_MATRIX / PUBLICCLOUD_AUTHORITY_MATRIX:

| FUNCTION | ONLYLAN AUTHORITY | PUBLICCLOUD AUTHORITY | LOCAL PROJECTION ROLE | FRONTEND READ SOURCE | FALLBACK |
|---|---|---|---|---|---|
| student join | LocalServer join mutation | Supabase PublicCloud join RPC | none for mutation | mode-selected response, then shared state | none |
| student lifecycle | LocalServer session/participant state | Public student timeline RPC | teacher-only read projection | shared lifecycle coordinator | none |
| resubmit permission | local `SessionParticipant` | cloud `session_participants` | teacher read projection only | `StudentSessionState.ResubmitAllowed` | false when field missing; no status inference |
| submission preparation | persistent local queue/spool | same persistent queue/spool | none | `SubmissionQueueStore` | no alternate queue |
| submission upload/finalize | LocalServer submission API | Supabase RPC/storage/verification | none for mutation | `SubmissionRecoveryService` mode dispatch | none |
| teacher participant visibility | local SQLite authority | cloud commit then SQLite projection | authoritative teacher read projection | LocalServer `GetSessionAsync` | no direct cloud plus SQLite dual read |

CROSS_MODE_FALLBACK_RESULT: PASS. No OnlyLAN-to-PublicCloud mutation fallback, PublicCloud-to-OnlyLAN mutation fallback, or direct-cloud-plus-SQLite teacher dual authority was found.

## Migration and Supabase source

MIGRATION_FILE: `backend/supabase/migrations/20260731113609_public_student_timeline_resubmit_allowed.sql`

MIGRATION_ORDER: PASS; newest of 27 migrations, unique timestamp, after required dependencies.

MIGRATION_SECURITY_RESULT: PASS; normalized comparison against the previous function definition has zero differences after removing only the new `resubmitAllowed` line. `SECURITY DEFINER`, empty `search_path`, tenant/student checks, revoke and authenticated grant are preserved. No historical migration changed.

MIGRATION_TEST_RESULT:

- PASS local/static: `backend/scripts/verify-supabase-source.ps1`;
- PASS contract scan for function/security/search_path/authority/revoke/grant;
- PASS source consistency: pgTAP plan is 39 and tests false then true authority projection;
- SKIP runtime pgTAP/local migration: Docker engine and local Supabase status were unavailable (`docker info` exit 1, `supabase status` exit 1). This task did not start/reset/mutate a local or remote database.

REMOTE_SUPABASE_ACTIONS: none. No `db push`, remote `migration up`, production RPC or production write was executed.

## Build and test evidence

FRONTEND_CLEAN_BUILD: PASS; Release clean/restore/build, 0 warnings, 0 errors.

BACKEND_CLEAN_BUILD: PASS; `backend/ExamTransfer.sln` Release clean/restore/build, 0 warnings, 0 errors.

FRONTEND_FULL_TESTS: PASS; 211 passed, 0 failed, 0 skipped.

BACKEND_FULL_TESTS: PASS; 253 passed, 0 failed, 1 existing skip, 254 total.

FOCUSED_INTEGRATED_TESTS: PASS; 173 passed, 0 failed, 0 skipped (139 frontend plus 34 backend). The file names from the task were mapped to their actual test class/FQN names so `RuntimeRebase03BTests.cs`, `RuntimeRebase03AR2CharacterizationTests.cs`, `SubmissionEligibilityAndSingleFlightTests.cs` and `RuntimeStabilize03E1SubmissionTests.cs` were not silently omitted by a filename-like filter.

AGGREGATE_RESULTS: PASS

- `ONLYLAN_BACKEND_CHARACTERIZATION_BUILD`: PASS, 0 warnings/errors;
- `ONLYLAN_CHARACTERIZATION_TARGETED`: PASS;
- `ONLYLAN_LOGIN_AUTH_FREEZE`: PASS;
- `ONLYLAN_PUBLICCLOUD_FREEZE`: PASS;
- `ONLYLAN_INFRASTRUCTURE_REGRESSION`: PASS;
- aggregate completion: PASS;
- full-solution step inside aggregate: SKIP by `not_requested`; the separately required frontend/backend Release builds above both PASS.

## Candidate/release script review

CANDIDATE_SCRIPT: FAIL

REASON: `CANDIDATE_SCRIPT_CONTRACT_BROKEN`

`scripts/build-onlylan-published-candidate.ps1` parses successfully and does preserve several required properties: one generated BuildId, `win-x64`, self-contained publish, fixed UDP 40550/TCP 5048 runtime contract, clean candidate output, SHA-256 manifest data and runtime BuildId health comparison. It nevertheless cannot be approved for the final candidate because:

1. It resolves HEAD but never checks tracked worktree cleanliness, while passing `ExamTransferWorkingTreeDirty=false` to publish and writing `workingTreeDirty=false` to the manifest unconditionally (`lines 5, 77, 110`). This can label a dirty build as clean.
2. Its pre-publish aggregate call omits `-RunPublishedE2E` (`line 26`), and after publish it runs only `/health` smoke (`lines 171-203`). The full `scripts/test-published-onlylan-e2e.ps1` exists but is never called by the candidate script.
3. Manifest provenance remains pinned to old tasks `ET-LAN-MODULE-REFACTOR-01D` and `ET-LAN-PUBLISHED-CANDIDATE-BUILD-ID-ATOMIC-01` (`lines 116-117`) instead of the final runtime integration/candidate chain.
4. The expected `scripts/release-consistency.ps1` is absent from HEAD and has no Git history, so no separate consistency gate compensates for these omissions.
5. Recursive cleanup uses an unvalidated relative candidate path. The current documented root makes the target narrow, but the script itself does not resolve and containment-check it before deletion.

No candidate script changed in `INTEGRATION_RANGE`; therefore the integrated runtime patches did not introduce this defect. The existing candidate workflow simply was not updated to the final integration contract.

BUILD_ID_CONTRACT: FAIL overall. Atomic single-ID propagation and runtime comparison are present, but source cleanliness is falsely hardcoded and therefore build identity/provenance is not trustworthy.

MANIFEST_CONTRACT: FAIL. Hash fields exist, but clean/dirty and task provenance can be false/stale.

RUNTIME_BUILD_ID_CONTRACT: PASS by static review; the published server receives the single BuildId and `/health` is compared against it. Not executed because candidate build is forbidden in 04A.

PUBLISHED_E2E_CONTRACT: FAIL; the candidate workflow does not invoke the available Published OnlyLAN E2E. Health-only smoke is not equivalent to join/approval/policy/quiz/finalize/teacher-observation/finish E2E.

PowerShell syntax/static validation: PASS for candidate, release, aggregate, Published E2E, installer guard, clean-install and public-config scripts. `scripts/build-release.ps1` propagates one BuildId to frontend/backend/manifest and validates public config, but it does not repair the separate final OnlyLAN candidate flow above.

## Final gate and verdict

DIFF_CHECK: PASS before and after report creation; final exit code 0.

TRACKED_WORKTREE_CLEAN: PASS before and after report creation; final status contains only the four allowed untracked reports.

REMAINING_MANUAL_RISKS:

- local pgTAP and migration runtime were not available because Docker/local Supabase were stopped;
- no real account, real multi-machine LAN, remote Supabase, candidate, installer or production validation was authorized or executed;
- Published OnlyLAN E2E was reviewed but not run because 04A forbids building/running a final candidate and the existing candidate artifact cannot be substituted for HEAD.

ROOT_CAUSE: runtime patches 03C-03E2 are integrated consistently and all automated source/build/test/aggregate gates pass. The final candidate workflow predates this integration and lacks clean-source attestation, current provenance and an invoked Published E2E gate.

OUT-OF-SCOPE FINDINGS: no production/test/schema repair was attempted. Candidate-script repair requires a separately authorized task.

FINAL_VERDICT:

`FINAL_INTEGRATION_NO_GO`

Blocking reason: `CANDIDATE_SCRIPT_CONTRACT_BROKEN`

NEXT_TASK:

`ET-RUNTIME-FINAL-CANDIDATE-SCRIPT-R1` — T1, C1 capability, R2. Repair and test only the candidate/release consistency wiring, then rerun `ET-RUNTIME-FINAL-INTEGRATION-04A`. Do not begin `ET-RUNTIME-FINAL-CANDIDATE-04B` while this verdict remains NO-GO.
