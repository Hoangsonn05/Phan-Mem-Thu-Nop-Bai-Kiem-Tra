# ET-RUNTIME-REBASE-03A-R3

## Final handoff

```text
RESULT: PASS
TASK: ET-RUNTIME-REBASE-03A-R3
MODEL/CAPABILITY/REASONING: GPT-5.6 Sol / C3 / R3 High

HEAD: 42813c4714e5805a737605972522a997f3d4a72b
WORKTREE_BEFORE:
  M frontend/tests/ExamTransfer.Desktop.Tests/LanRoomJoinAndLifecycleTests.cs
  M frontend/tests/ExamTransfer.Desktop.Tests/SixFindingsV133Tests.cs
  ?? frontend/tests/ExamTransfer.Desktop.Tests/RuntimeRebase03AR2CharacterizationTests.cs
  ?? backend/tests/ExamTransfer.Infrastructure.Tests/RuntimeRebase03AR2SubmissionCharacterizationTests.cs
  ?? ET-RUNTIME-REBASE-03A_REPORT.md
WORKTREE_AFTER:
  M frontend/tests/ExamTransfer.Desktop.Tests/LanRoomJoinAndLifecycleTests.cs
  M frontend/tests/ExamTransfer.Desktop.Tests/SixFindingsV133Tests.cs
  ?? frontend/tests/ExamTransfer.Desktop.Tests/RuntimeRebase03AR2CharacterizationTests.cs
  ?? backend/tests/ExamTransfer.Infrastructure.Tests/RuntimeRebase03AR2SubmissionCharacterizationTests.cs
  ?? ET-RUNTIME-REBASE-03A_REPORT.md

PRODUCTION_FILES_CHANGED: NONE
TEST_FILES_CHANGED:
  R3 changed only frontend/tests/ExamTransfer.Desktop.Tests/LanRoomJoinAndLifecycleTests.cs
  inherited R2 test files remain in the worktree; the backend characterization file was not changed by R3
REPORT_FILES_CHANGED: ET-RUNTIME-REBASE-03A_REPORT.md

LAN_FIXTURE_ROOT_CAUSE:
  JoinRecordingHandler expected JSON property requestId, but the production contract calls
  the request identity Nonce and JsonSerializerDefaults.Web serializes it as nonce.
LAN_FIXTURE_CORRECTION:
  deserialize the actual JoinSessionRequest DTO with the same Web JSON options, reject a
  missing/blank Nonce, and record JoinNonces for retry identity assertions
LAN_REQUEST_CONTRACT:
  JoinSessionRequest(RoomCode, StudentCode, DisplayName, ClassName, DeviceId, MachineName,
  AppVersion, Nonce) -> roomCode, studentCode, displayName, className, deviceId, machineName,
  appVersion, nonce; no sessionId or requestId property is sent by this call
LAN_FAILED_TESTS_BEFORE:
  FAIL LanPostJoinSynchronizationFailure_RetainsIdentityButReportsJoinAsNetworkFailure
  FAIL LanRejoinAfterPostJoinFailure_ReissuesMutationWithNewRequestIdentity
LAN_TESTS_AFTER:
  PASS 1/1 LanPostJoinSynchronizationFailure_RetainsIdentityButReportsJoinAsNetworkFailure
  PASS 1/1 LanRejoinAfterPostJoinFailure_ReissuesMutationWithNewRequestIdentity

PUBLICCLOUD_FIXTURE_ROOT_CAUSE:
  positional PublicCloudJoinResult construction supplied ParticipantId in the ExamId slot
  and ExamId in the ParticipantId slot
PUBLICCLOUD_FIXTURE_CORRECTION:
  construct PublicCloudJoinResult with exact named arguments and assert SessionId, ExamId,
  and ParticipantId before and after retry
PUBLICCLOUD_JOIN_RESULT_CONTRACT:
  SessionId, ExamId, ParticipantId, ParticipantStatus, SessionStatus, RoomCode, ExamTitle,
  Subject, DurationMinutes, DeliveryType, SupervisionMode, QuizResultPolicy, PlannedStartUtc,
  Capacity, CurrentParticipantCount, AccessToken
PUBLICCLOUD_FAILED_TESTS_BEFORE:
  FAIL PublicCloudPostJoinSynchronizationFailure_RetainsIdentityAndReissuesRpcOnRetry
PUBLICCLOUD_TESTS_AFTER:
  PASS 1/1 PublicCloudPostJoinSynchronizationFailure_RetainsIdentityAndReissuesRpcOnRetry

ONLYLAN_FALSE_NEGATIVE_JOIN: PROVEN
ONLYLAN_POST_JOIN_STATE:
  authoritative join response reaches ApplyJoin; SessionId, ParticipantId, and access token
  remain stored after the injected completion failure; UI reports NETWORK_ERROR/danger
ONLYLAN_REJOIN_DUPLICATE:
  NOT_REPRODUCED; frontend retries the authority mutation with two distinct Nonce values,
  while the characterized authority response retains the same ParticipantId

PUBLICCLOUD_FALSE_NEGATIVE_JOIN: PROVEN
PUBLICCLOUD_POST_JOIN_STATE:
  RPC identity is copied to state before the injected completion failure; SessionId, ExamId,
  ParticipantId, and access token remain correct while UI reports NETWORK_ERROR
PUBLICCLOUD_REJOIN_DUPLICATE:
  NOT_REPRODUCED; retry reissues the RPC and the characterized result retains the same full
  identity; no live/staging RPC was run in R3

COLLECTING_ROUTE: PROVEN maps to ApprovedWaiting / S-03
TERMINAL_TIMER: PROVEN continues for Finished, Cancelled, and Archived
SUBMIT_COMMAND_ELIGIBILITY:
  PROVEN fail-open relative to lifecycle; it depends on file/busy/HasSession and remains
  executable for pending/waiting/terminal/submitted/quiz characterization cases
SEQUENTIAL_QUEUE_DUPLICATION:
  PROVEN at the local deterministic source/command boundary: immediate command spam is
  single-flight, sequential executions all run, active queue does not disable Submit, and
  PrepareAsync creates fresh queue/key/directory without SessionId+ParticipantId lookup
CHUNK_AFTER_END: PROVEN accepted
FINALIZE_AFTER_END: PROVEN accepted and creates the receipt/submitted state
CONCURRENT_INIT_DIFFERENT_KEY: PROVEN one active row plus one raw database failure
CONCURRENT_INIT_SAME_KEY: PROVEN one active row plus one raw database failure
FINALIZE_IDEMPOTENCY: PROVEN same receipt and one receipt artifact

PUBLICCLOUD_STAGING_AVAILABLE: false
PUBLICCLOUD_TEACHER_VISIBILITY_CONFIDENCE:
  STRONGLY_SUPPORTED for pull delay plus missing post-pull UI refresh; exact projection
  latency and live teacher-visibility root cause remain UNKNOWN without staging timing

THREE_FAILED_TESTS_RESULT: PASS 3/3 after fixture correction; each test was run separately
TARGETED_FRONTEND_RESULTS: PASS 38/38; 0 failed; 0 skipped
FULL_FRONTEND_RESULTS: PASS 148/148; 0 failed; 0 skipped
BACKEND_CHARACTERIZATION_RESULTS: PASS 7/7; baseline preserved
FULL_BACKEND_RESULTS: PASS 226; 0 failed; 1 skipped RealUserDocx user-fixture test
AGGREGATE_RESULTS:
  PASS ONLYLAN_CHARACTERIZATION_VERIFICATION_COMPLETE; publishedE2E=False; fullSolution=False;
  the script intentionally reported ONLYLAN_FULL_SOLUTION_BUILD SKIP reason=not_requested
DIFF_CHECK: PASS
PRODUCTION_GUARD: PASS; no frontend/src, backend/src, or supabase worktree diff

REGRESSION_CREATED_BY_REFACTOR: NOT_REPRODUCED
REGRESSION_PRESERVED_BY_REFACTOR:
  post-join false-negative classification, Collecting route, terminal timer, fail-open submit
  eligibility, sequential queue creation, terminal chunk/finalize acceptance, concurrent init
  failures, and PublicCloud teacher refresh risk all remain preserved/pre-existing behaviors

PATCH_03C_STATUS: AUTHORIZED_FOR_PLANNING; NOT STARTED
PATCH_03D_STATUS: AUTHORIZED_FOR_PLANNING; NOT STARTED
PATCH_03E1_STATUS: AUTHORIZED_FOR_PLANNING; NOT STARTED
PATCH_03E2_STATUS: REQUIRES_STAGING_OR_SEPARATE_SOURCE-BASED_DESIGN_REVIEW; NOT STARTED

REMAINING_UNKNOWN:
  executable-driven WPF runtime acceptance; exact PublicCloud T0/T1/T2/T3 projection latency;
  live teacher visibility root cause; live multi-machine/staging behavior
FINAL_VERDICT:
  PASS - both fixture defects match the production contracts, all required local gates pass,
  no production source changed, and staging-only findings were not promoted to PROVEN.
NEXT_TASK:
  STOP. Await an explicit separately scoped task/approval before planning or implementing
  03C, 03D, 03E1, or 03E2. Do not stage, commit, build a candidate, or run live Supabase.
```

## R3 evidence summary

The LAN fixture now reads the request through `JoinSessionRequest`, which is the
same shared DTO sent by `BackendClient`. The two successful LAN tests establish
that `ApplyJoin` precedes post-join completion and that a completion exception is
reported as `NETWORK_ERROR` without clearing the joined identity. The retry test
also records two distinct production `Nonce` values.

The PublicCloud fixture now uses named constructor arguments matching
`PublicCloudJoinResult(SessionId, ExamId, ParticipantId, ...)`. Its test verifies
all three identity fields before and after the second RPC call, while the injected
post-join exception remains intact.

No Published E2E, candidate/installer build, full-solution aggregate build,
staging RPC, production Supabase call, schema/RPC/RLS change, stage, or commit was
performed.

---

# Historical ET-RUNTIME-REBASE-03A-R2 handoff

## Mandatory handoff

```text
RESULT: FAIL
TASK: ET-RUNTIME-REBASE-03A-R2
MODEL/CAPABILITY/REASONING: Codex based on GPT-5 / C3-C4 / R3 High

HEAD: 42813c4714e5805a737605972522a997f3d4a72b
HEAD_COMMIT_MESSAGE: Restore frontend logging and submission progress contracts
WORKTREE_BEFORE: tracked clean; only ?? ET-RUNTIME-REBASE-03A_REPORT.md
WORKTREE_AFTER:
  M frontend/tests/ExamTransfer.Desktop.Tests/LanRoomJoinAndLifecycleTests.cs
  M frontend/tests/ExamTransfer.Desktop.Tests/SixFindingsV133Tests.cs
  ?? backend/tests/ExamTransfer.Infrastructure.Tests/RuntimeRebase03AR2SubmissionCharacterizationTests.cs
  ?? frontend/tests/ExamTransfer.Desktop.Tests/RuntimeRebase03AR2CharacterizationTests.cs
  ?? ET-RUNTIME-REBASE-03A_REPORT.md

FRONTEND_BUILD_RESULT: PASS; 0 warnings; 0 errors
BINARY_PATH: frontend/src/ExamTransfer.Desktop/bin/Release/net10.0-windows/ExamTransfer.Desktop.exe
BINARY_SHA256: EE0F91BCE41D65B99A90D4AB97E9371EBEF13E1F33076A50F5387C27755A3682
HEAD_BINARY_PROVENANCE: PROVEN
  build start: 2026-07-31T14:59:22.3928573+07:00
  build finish: 2026-07-31T14:59:29.3421870+07:00
  binary timestamp: 2026-07-31T14:59:29.2003539+07:00
  old win-x64 binary timestamped 2026-07-30 was rejected

SLICE1_8_BASELINE: parent 3dfe77b2041e3239a064039020e00fdc0c386d08
  Slice 1: f51108f session approval execution
  Slice 2: 151d6f6 remaining participant mutations
  Slice 3: ba66861 submission mutations
  Slice 4: 7bbc82e OnlyLAN join/heartbeat
  Slice 5: 947f5d0 PublicCloud projection readiness
  Slice 6: 76502b8 device status read
  Slice 7: 8dd2c3f quiz projection outbox
  Slice 8: 7d27e49 facade dependency cleanup
  08ebef5 is a build script commit, not an extraction slice

JOIN_REFACTOR_COMPARISON: BEHAVIOR_PRESERVED
HEARTBEAT_REFACTOR_COMPARISON: BEHAVIOR_PRESERVED
SUBMISSION_REFACTOR_COMPARISON: BEHAVIOR_PRESERVED for Init/Upload/Finalize;
  Reject/AllowResubmit alone moved to mode handlers/dispatcher
FRONTEND_REFACTOR_COMPARISON: no Slice 1-8 changes; required frontend blobs are identical
PULL_WORKER_REFACTOR_COMPARISON: no Slice 1-8 changes; blob is identical

ONLYLAN_JOIN_MUTATION_RESULT: success path persists identity before post-join completion
ONLYLAN_POST_JOIN_SYNC_RESULT: source shows realtime/resolve can throw after ApplyJoin
ONLYLAN_FALSE_NEGATIVE_JOIN: STRONGLY_SUPPORTED; UI maps the later exception to NETWORK_ERROR
ONLYLAN_REJOIN_DUPLICATE: NOT_REPRODUCED at backend source boundary; existing participant is reused

PUBLICCLOUD_JOIN_MUTATION_RESULT: RPC result is copied to state before post-join completion
PUBLICCLOUD_POST_JOIN_SYNC_RESULT: source shows realtime/timeline can throw after identity update
PUBLICCLOUD_FALSE_NEGATIVE_JOIN: STRONGLY_SUPPORTED; UI maps the later exception to NETWORK_ERROR
PUBLICCLOUD_REJOIN_DUPLICATE: NOT_REPRODUCED by RPC source and existing pgTAP fixture;
  same user/device returns the same participant, but pgTAP was not rerun in this failed task

APPROVAL_NAVIGATION: deterministic snapshot characterization PASS
APPROVAL_NOTIFICATION: one navigation for three same-revision realtime notifications PASS;
  no separate user-notification contract was found
FILE_START_NAVIGATION: route S-05, RequiresStartConfirmation=false; navigation is published
QUIZ_START_NAVIGATION: route S-06, RequiresStartConfirmation=true
FINISH_NAVIGATION: route S-04
CANCEL_NAVIGATION: route S-04
NAVIGATION_IDEMPOTENCY: PASS for repeated same revision through StudentWaitingViewModel

TIMER_FINISHED: PROVEN continued
TIMER_CANCELLED: PROVEN continued
TIMER_ARCHIVED: PROVEN continued
TERMINAL_STATE_COMMAND_GUARD: PROVEN absent from SubmitCommand

SUBMIT_COMMAND_ELIGIBILITY:
  actual predicate is !IsBusy && IsFileValid && SelectedPath != blank && state.HasSession
  it does not inspect participant approval, session phase, delivery type,
  existing submission, active queue, or resubmit permission
SUBMIT_SPAM_RESULT:
  immediate triple ICommand execution is single-flight at AsyncRelayCommand
  sequential execution runs three times after IsBusy/isRunning clears
QUEUE_ID_COUNT: immediate command-level=1; sequential source behavior=3 fresh IDs
IDEMPOTENCY_KEY_COUNT: immediate command-level=1; sequential source behavior=3 fresh keys
SPOOL_DIRECTORY_COUNT: immediate command-level=1; sequential source behavior=3 fresh directories
RECOVERY_TRIGGER_COUNT: immediate command-level=1; sequential source behavior=3 triggers
PROGRESS_SNAPSHOT_RESULT:
  03B event snapshot contract remains present; receipt alone reaches 100%;
  failed is terminal below 100%; terminal unsubscribes; no polling method remains
  03B focused progress tests were not rerun after the mandatory two-round stop

INIT_AFTER_END: initial init succeeds before end
CHUNK_AFTER_END: PROVEN accepted
FINALIZE_AFTER_END: PROVEN accepted; receipt created and participant/submission become submitted
CONCURRENT_INIT_DIFFERENT_KEY:
  PROVEN one active row plus one raw database failure
CONCURRENT_INIT_SAME_KEY:
  PROVEN one active row plus one raw database failure instead of idempotent response
FINALIZE_IDEMPOTENCY:
  PROVEN same receipt code/signature/timestamp and one receipt artifact

PUBLICCLOUD_STAGING_AVAILABLE: false
PUBLICCLOUD_RPC_JOIN: BLOCKED_BY_SAFE_STAGING_UNAVAILABLE
PUBLICCLOUD_PROJECTION_LATENCY: BLOCKED_BY_SAFE_STAGING_UNAVAILABLE
PUBLICCLOUD_PULL_CURSOR: source uses CloudVersion/UpdatedAt/EntityId tuple
PUBLICCLOUD_PULL_RESULT:
  source interval 5s; batch 100; up to 10 pages; entity order includes session_participants;
  failure retry schedule 5/15/30/60/120/300 and outer-cycle delay 15s
TEACHER_REFRESH_RESULT:
  cloud realtime schedules an immediate Local Server snapshot refresh;
  pull projection emits no later local refresh/pulse
TEACHER_VISIBILITY_ROOT_CAUSE:
  PULL_WORKER_DELAYED + TEACHER_UI_NOT_REFRESHED are STRONGLY_SUPPORTED;
  runtime root cause remains UNKNOWN without staging timing

REGRESSION_CREATED_BY_REFACTOR: NOT_REPRODUCED
REGRESSION_PRESERVED_BY_REFACTOR:
  post-join classification, terminal timer, eligibility/single-flight,
  terminal upload/finalize, concurrent init, and teacher refresh behaviors
  all predate or are outside Slice 1-8
PREEXISTING_DEFECTS:
  post-join sync reported as mutation/network failure
  Collecting maps back to ApprovedWaiting/S-03
  timer continues in Finished/Cancelled/Archived
  SubmitCommand eligibility is fail-open relative to lifecycle
  sequential submit creates fresh queue identity/spool
  chunk/finalize accept after session end
  concurrent init leaks raw database failure, including same idempotency key
  teacher refresh can race ahead of PublicCloud pull without a post-pull pulse

SCHEMA_CHANGE_REQUIRED: NO on current evidence
RPC_CHANGE_REQUIRED: NO on current evidence
MIGRATION_CHANGE_REQUIRED: NO on current evidence

FRONTEND_TEST_RESULTS:
  targeted build PASS
  targeted run: 24 PASS / 3 FAIL / 0 SKIP
  three FAIL are characterization harness defects:
    LAN handler consumed request JSON with the wrong property assumption,
    so post-join completion was never reached and request IDs were not recorded
    PublicCloudJoinResult fixture swapped ExamId and ParticipantId constructor positions
  each failed again in isolation; parallel-test interference was disproved
  no third patch was attempted
BACKEND_TEST_RESULTS:
  RuntimeRebase03AR2SubmissionCharacterizationTests: 7/7 PASS
AGGREGATE_RESULTS: SKIP after two failed frontend repair rounds
DIFF_CHECK: PASS after final report rewrite; untracked whitespace check PASS

OUT_OF_SCOPE_FINDINGS:
  no production source was changed
  no live/staging Supabase was available
  no installer, candidate, E2E, schema, migration, RPC, RLS, auth, stage, or commit action ran
REMAINING_UNKNOWN:
  executable-driven WPF runtime acceptance
  exact PublicCloud T0/T1/T2/T3 latency
  live teacher visibility root cause
  corrected deterministic join-harness results
  full frontend/backend/aggregate regression after harness correction
FINAL_VERDICT:
  FAIL - characterization implementation is incomplete because three new join tests fail.
  Backend findings are valid local deterministic evidence; staging findings remain blocked.
  This FAIL does not authorize a production patch.
NEXT_TASK:
  ET-RUNTIME-REBASE-03A-R3 independent review.
  T3 / C3 / R3 High.
  Correct the two test-fixture defects only, rerun targeted/full/aggregate gates,
  then finalize characterization. Do not start 03C/03D/03E.
```

## Refactor comparison

### Join

The `7bbc82e` diff moves the existing sequence from `SessionService.JoinAsync`
to `LanParticipantSessionExecution.JoinAsync`:

1. validate request/account identity;
2. normalize and load session/exam/participants;
3. reject PublicCloud at the Local Server route;
4. require Waiting plus accepting participants;
5. apply LAN access policy;
6. enforce class membership and capacity;
7. reuse the same student/device participant or create one;
8. `SaveChangesAsync`;
9. existing rejoin: outbox then token/DTO;
10. new join: audit, outbox, realtime, token/DTO.

No ordering change attributable to the extraction was found.

### Heartbeat

The same commit moves participant lookup, device validation, `LastSeenUtc`,
disconnected-state restoration, sequence increment, save, conditional realtime,
and server timestamp response without a behavior change.

### Submission

The `ba66861` diff delegates only `RejectAsync` and `AllowResubmitAsync`.
`InitAsync`, `UploadChunkAsync`, `FinalizeAsync`, receipt creation, upload
storage, and file verification are unchanged. LAN mutation side-effect order
and PublicCloud RPC/no-local-write behavior are preserved by the handlers.

### Frontend and pull worker

Git blob hashes at `3dfe77b` and `7d27e49` are identical for:

- `StudentConnectViewModel`
- `StudentExamFlowCoordinator`
- `StudentExamViewModel`
- `LiveMonitorViewModel`
- `SupabasePublicCloudClient`
- `PublicCloudPullWorker`

Commit `42813c4` is separately classified as the 03B compile/progress repair.

## Root-cause matrix

| Symptom | Mode | Layer | Root cause | Evidence | Confidence | Created by refactor | Preserved by refactor | Patch boundary | Schema/RPC |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Join looks failed after mutation succeeded | Both | Frontend lifecycle | one try/catch covers mutation and later realtime/resolve | state update precedes completion; generic catch maps to NETWORK_ERROR | STRONGLY_SUPPORTED | No | Yes | existing `StudentLifecycleCoordinator` plus mode authority result | No |
| Retry calls join again | Both | Frontend lifecycle | no committed-mutation outcome retained separately | join command has no post-mutation checkpoint | STRONGLY_SUPPORTED | No | Yes | `StudentLifecycleCoordinator` | No |
| Duplicate participant on rejoin | Both | Authority | LAN reuses student/device; cloud RPC locks and returns existing user/device row | backend and SQL source; existing pgTAP fixture | NOT_REPRODUCED | No | No | retain authority rules | No |
| Collecting returns waiting route | Both | Frontend state machine | only InProgress/Paused are active; Collecting falls through | passing snapshot characterization | PROVEN | No | Yes | `StudentLifecycleCoordinator` state map | No |
| Terminal timer continues | Both | Frontend timer | ticker starts in constructor and stops only on Dispose | three passing deterministic tests | PROVEN | No | Yes | lifecycle coordinator drives timer ownership | No |
| Submit enabled in invalid lifecycle states | Both | Frontend eligibility | predicate checks only file/busy/HasSession | passing eligibility matrix | PROVEN | No | Yes | `SubmissionEligibilityPolicy` | No |
| Sequential submit creates new queue work | Both | Frontend queue | each prepare generates queue/key/spool; no active lookup | command test plus source characterization | STRONGLY_SUPPORTED | No | Yes | `SubmissionQueueSingleFlight` | No |
| Upload/finalize after teacher end | OnlyLAN backend | Submission service | chunk/finalize validate submission status, not terminal session | passing end-mid-upload test | PROVEN | No | Yes | `OnlyLanSubmissionAuthority` or shared eligibility guard | No |
| Concurrent init raw failure | OnlyLAN backend | Submission service/SQLite | read-before-insert attempt allocation; unique indexes surface raw race | two passing barrier tests | PROVEN | No | Yes | shared atomic init boundary | No |
| Teacher misses newly joined cloud participant | PublicCloud | Pull/UI refresh | realtime refresh can precede 5s pull; no post-pull UI pulse | source pipeline analysis | STRONGLY_SUPPORTED | No | Yes | projection completion signal + UI refresh | No |

## Recommended shared boundaries

| Boundary | Decision | Evidence-based role |
| --- | --- | --- |
| `StudentLifecycleCoordinator` | NEEDED | extend the existing boundary to separate authoritative mutation success from post-join synchronization and own terminal navigation/timer |
| `OnlyLanStudentSessionAuthority` | NEEDED | normalize Local Server join/snapshot result without cloud fallback |
| `PublicCloudStudentSessionAuthority` | NEEDED | normalize RPC/timeline result while retaining stable participant identity |
| `SubmissionEligibilityPolicy` | NEEDED | one lifecycle policy shared by CanExecute and execution |
| `SubmissionQueueSingleFlight` | NEEDED | deduplicate by SessionId+ParticipantId before creating queue/key/spool |
| `SubmissionProgressSnapshot` | NOT_NEEDED | 03B already established the event snapshot contract |
| `OnlyLanSubmissionAuthority` | DEFER | terminal/concurrent backend fix boundary needs review; do not add abstraction only for naming |
| `PublicCloudSubmissionAuthority` | DEFER | no local runtime evidence justifies a new class in this task |

```text
PATCH_GROUP_03C:
  Student lifecycle outcome classification, Collecting/terminal routing,
  timer ownership, and corrected deterministic join tests.

PATCH_GROUP_03D:
  SubmissionEligibilityPolicy and SubmissionQueueSingleFlight with exact
  QueueId/IdempotencyKey/spool/recovery/subscription runtime measurements.

PATCH_GROUP_03E:
  Atomic backend init and terminal-session upload/finalize guards, followed by
  a separate projection-completion refresh design for PublicCloud teacher UI.

No patch group is authorized by this report.
```

## Verification log

```text
PASS git start gate
PASS dotnet clean frontend Desktop Release
PASS dotnet restore frontend Desktop
PASS dotnet build frontend Desktop Release --no-restore (0 warning, 0 error)
PASS backend targeted characterization 7/7
FAIL frontend targeted characterization 24/27
FAIL three join harness tests individually
PASS git diff --check after final report rewrite
SKIP frontend full suite after mandatory two-round stop
SKIP backend full suite after mandatory two-round stop
SKIP aggregate characterization after mandatory two-round stop
SKIP staging/live Supabase: safe staging unavailable
```
