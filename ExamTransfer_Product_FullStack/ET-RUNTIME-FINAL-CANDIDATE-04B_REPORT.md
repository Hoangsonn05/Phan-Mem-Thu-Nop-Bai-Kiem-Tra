# ET-RUNTIME-FINAL-CANDIDATE-04B REPORT

RESULT: `PASS`

TASK: `ET-RUNTIME-FINAL-CANDIDATE-04B`

MODEL/CAPABILITY/REASONING: Codex 5.6 Sol / C3 / R3 High

HEAD_BEFORE: `5085dfbdda251f7311e9340c138b2a17a0167471`

HEAD_AFTER: `5085dfbdda251f7311e9340c138b2a17a0167471`

FINAL_INTEGRATION_HEAD: `5085dfbdda251f7311e9340c138b2a17a0167471`

HEAD_MATCH: `PASS`

WORKTREE_BEFORE: tracked clean; five allowed untracked reports only.

WORKTREE_AFTER: tracked clean; the five pre-existing allowed untracked reports plus this 04B report. Candidate artifacts and verification logs are ignored build outputs.

VERSION_SOURCE: `Directory.Build.props`; cross-checked as `1.2.0` in `backend/Directory.Build.props`, `frontend/src/ExamTransfer.Desktop/ExamTransfer.Desktop.csproj`, and `installer/ExamTransfer.iss`. The candidate script reads `Directory.Build.props` directly and has no `-Version` parameter.

VERSION: `1.2.0`

BUILD_ID: `1.2.0+5085dfbd-onlylan-final.20260731T151225Z`

BUILD_START_UTC: `2026-07-31T15:12:25.4334395+00:00`

BUILD_FINISH_UTC: `2026-07-31T15:13:42.5372261+00:00`

CANDIDATE_SCRIPT: `scripts/build-onlylan-published-candidate.ps1`

SCRIPT_PARAMETERS: `-PublishedE2ERepeat 3`; default project root and default containment-checked candidate output. Effective RID `win-x64`; self-contained `true`; TCP `5048`; UDP `40550`.

CANDIDATE_OUTPUT: `D:\MMO\PhanMemNopThuBaiKiemTra\ExamTransfer_Product_FullStack\artifacts\onlylan-published-e2e\candidate`

CLIENT_DIRECTORY: `D:\MMO\PhanMemNopThuBaiKiemTra\ExamTransfer_Product_FullStack\artifacts\onlylan-published-e2e\candidate\Client`

SERVER_DIRECTORY: `D:\MMO\PhanMemNopThuBaiKiemTra\ExamTransfer_Product_FullStack\artifacts\onlylan-published-e2e\candidate\Server`

MANIFEST_PATH: `D:\MMO\PhanMemNopThuBaiKiemTra\ExamTransfer_Product_FullStack\artifacts\onlylan-published-e2e\candidate\release-manifest.json`

ARTIFACT_INVENTORY: 373 files, 384 total recursive entries, 196,343,352 bytes. Top-level entries are `Client`, `Server`, `build-logs`, `smoke`, and `release-manifest.json`.

FRONTEND_EXE: `D:\MMO\PhanMemNopThuBaiKiemTra\ExamTransfer_Product_FullStack\artifacts\onlylan-published-e2e\candidate\Client\ExamTransfer.Desktop.exe`

FRONTEND_SIZE: `65743467`

FRONTEND_SHA256: `857E6A27BA6F1A85509CDF70132DF41BFDD986CB6271D6F0C8E9CF626F4DE6A6`

FRONTEND_LAST_WRITE_UTC: `2026-07-31T15:13:10.9728533Z`; inside manifest build window.

BACKEND_EXE: `D:\MMO\PhanMemNopThuBaiKiemTra\ExamTransfer_Product_FullStack\artifacts\onlylan-published-e2e\candidate\Server\ExamTransfer.LocalServer.exe`

BACKEND_SIZE: `162816`

BACKEND_SHA256: `DFEF56D43732990F2F0FF17E2094857DB67C7BCE2A6D111545A42D816B365A0A`

BACKEND_LAST_WRITE_UTC: `2026-07-31T15:13:15.2731705Z`; inside manifest build window.

MANIFEST_SHA256: `81C0318C7375732C9B0197E33F9477F3DFF8059345BB7CA4645D9AA8FBFED5DA`

PACKAGE: `NONE`

PACKAGE_SIZE: `N/A`

PACKAGE_SHA256: `N/A`

MANIFEST_GIT_HEAD: `5085dfbdda251f7311e9340c138b2a17a0167471`

MANIFEST_VERSION: `1.2.0`

MANIFEST_WORKTREE_DIRTY: boolean `false`

MANIFEST_INTEGRATION_GATE: `ET-RUNTIME-FINAL-INTEGRATION-04A`

MANIFEST_CANDIDATE_TASK: `ET-RUNTIME-FINAL-CANDIDATE-04B`

MANIFEST_PATCH_CHAIN:

1. `ET-RUNTIME-STABILIZE-03C`
2. `ET-RUNTIME-STABILIZE-03D-AUTHORITY-01`
3. `ET-RUNTIME-STABILIZE-03D-R1`
4. `ET-RUNTIME-STABILIZE-03E1`
5. `ET-RUNTIME-STABILIZE-03E2-R1`

MANIFEST_RID: `win-x64`

MANIFEST_SELF_CONTAINED: boolean `true`

BUILD_ID_BUILD_LOG: `1.2.0+5085dfbd-onlylan-final.20260731T151225Z`

BUILD_ID_MANIFEST: `1.2.0+5085dfbd-onlylan-final.20260731T151225Z`

BUILD_ID_FRONTEND: `1.2.0+5085dfbd-onlylan-final.20260731T151225Z` passed once to the frontend publish property; the 32-assertion contract harness proves the same single BuildId is passed to both publishes. No separate GUI runtime metadata surface was invoked.

BUILD_ID_BACKEND_HEALTH: `1.2.0+5085dfbd-onlylan-final.20260731T151225Z`; the candidate script accepted the HTTP health response only after exact equality and passed this value as `RuntimeBuildId` to release consistency.

BUILD_ID_CONSISTENCY: `PASS`; one assignment, two publish propagations, manifest equality, Published E2E identity, backend health equality, and release consistency all use the same value.

RUNTIME_HEALTH_RESULT: `PASS`; published `Server/ExamTransfer.LocalServer.exe` started, returned HTTP success, `BACKEND_RUNTIME_READY`, `UDP_DISCOVERY_LISTENING`, protocol `ExamTransfer/2`, port `40550`, and the exact candidate BuildId. Candidate-owned process was stopped by the workflow.

RUNTIME_HEALTH_URL: `http://127.0.0.1:5048/health`

TCP_PORT: `5048`

UDP_PORT: `40550`

PUBLISHED_E2E_COMMAND: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-published-onlylan-e2e.ps1 -ServerDirectory <candidate>\Server -ClientDirectory <candidate>\Client -Repeat 3`

PUBLISHED_E2E_EXIT_CODE: `0`

PUBLISHED_E2E_CASES: `LAN_E2E_DISCOVERY`, `LAN_E2E_JOIN_PENDING`, `LAN_E2E_APPROVED`, `ONLYLAN_POLICY_ACK_OK`, `LAN_E2E_POLICY_APPLIED`, `LAN_E2E_QUIZ_FINALIZED`, `LAN_E2E_FINISHED`; all completed for iterations 1, 2, and 3. Each iteration also emitted `ONLYLAN_E2E_OK` for this BuildId.

PUBLISHED_E2E_PASS_COUNT: 18 named `PASS LAN_E2E_*` assertions, 3 policy acknowledgements, and 3/3 completed iterations.

PUBLISHED_E2E_FAIL_COUNT: `0`

PUBLISHED_E2E_RESULT: `PASS`; final marker `ONLYLAN_E2E_REPEAT_OK repeat=3`.

RELEASE_CONSISTENCY_COMMAND: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/release-consistency.ps1 -CandidateRoot <candidate> -ManifestPath <candidate>\release-manifest.json -RepositoryRoot D:\MMO\PhanMemNopThuBaiKiemTra -ExpectedHead 5085dfbdda251f7311e9340c138b2a17a0167471 -ExpectedBuildId 1.2.0+5085dfbd-onlylan-final.20260731T151225Z -RuntimeBuildId 1.2.0+5085dfbd-onlylan-final.20260731T151225Z`

RELEASE_CONSISTENCY_RESULT: `PASS`

SCRIPT_CONTRACT_HARNESS: `PASS`, 32/32 assertions. PowerShell parse check also passed for all four candidate/E2E/consistency/harness scripts.

FRONTEND_FULL_TESTS: `PASS`; 211 passed, 0 failed, 0 skipped, exit code 0.

BACKEND_FULL_TESTS: `PASS`; 253 passed, 0 failed, 1 existing skip, 254 total, exit code 0.

AGGREGATE_RESULTS: `PASS`; `ONLYLAN_BACKEND_CHARACTERIZATION_BUILD`, `ONLYLAN_CHARACTERIZATION_TARGETED`, `ONLYLAN_LOGIN_AUTH_FREEZE`, `ONLYLAN_PUBLICCLOUD_FREEZE`, and `ONLYLAN_INFRASTRUCTURE_REGRESSION` passed. Aggregate full-solution build was its expected `not_requested` skip; the separately required frontend and backend Release test/build gates passed.

MIGRATION_INCLUDED_IN_SOURCE: `YES`; `backend/supabase/migrations/20260731113609_public_student_timeline_resubmit_allowed.sql` exists at the exact candidate source HEAD.

PUBLICCLOUD_MIGRATION_REMOTE_STATUS: `NOT APPLIED BY THIS TASK`

PRODUCTION_SUPABASE_ACTIONS: `NONE`

TRACKED_WORKTREE_CLEAN_AFTER: `PASS`; zero tracked porcelain entries.

DIFF_CHECK: `PASS`; exit code 0 before build, after candidate/regression, and after report creation.

COMMANDS_AND_RESULTS:

- `git rev-parse --show-toplevel`, `git rev-parse HEAD`, `git log -5 --oneline`, `git status --short`, `git diff --check`: start and final gates PASS.
- PowerShell parser over the four candidate scripts: 4/4 PASS.
- `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-final-candidate-contract.ps1`: 32/32 PASS.
- `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-onlylan-published-candidate.ps1 -PublishedE2ERepeat 3`: exit 0; candidate, health, Published E2E, and consistency PASS.
- `dotnet test frontend/tests/ExamTransfer.Desktop.Tests/ExamTransfer.Desktop.Tests.csproj -c Release`: 211/211 PASS.
- `dotnet test backend/tests/ExamTransfer.Infrastructure.Tests/ExamTransfer.Infrastructure.Tests.csproj -c Release`: 253 PASS, 1 existing SKIP.
- `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-onlylan-characterization-backend-collect.ps1`: exit 0; aggregate PASS.
- One optional independent health recheck command was rejected by command policy before execution. It created no process or file and was not substituted for the real candidate health gate, which had already passed and supplied the RuntimeBuildId to release consistency.

ROOT_CAUSE: no candidate-build defect occurred. The prior candidate-workflow blocker had already been corrected in commit `5085dfb`; this task consumed that approved immutable HEAD and produced one verified candidate.

FILES_CREATED_OR_UPDATED: candidate output plus local build/E2E/regression logs under ignored `artifacts`; this untracked 04B report. No tracked source, test, script, schema, migration, package, or installer file changed.

STABLE_AREAS_VERIFIED: candidate script contract, frontend and backend publish identity, published runtime health, three-repeat Published OnlyLAN E2E, release consistency, frontend full tests, backend full tests, aggregate authority/characterization gates, migration source inclusion, and tracked repository immutability.

OUT-OF-SCOPE_FINDINGS: the pre-existing untracked `ET-RUNTIME-FINAL-INTEGRATION-04A_REPORT.md` describes the earlier HEAD `724f34d` before the candidate-script repair; it was not modified or used as current candidate evidence. No source repair was attempted.

PATCH_SOURCE_PLAN: `COMPLETED`

FINAL_INTEGRATION: `GO`

FINAL_CANDIDATE: `LOCAL_GO`

MANUAL_ONLYLAN: `DEFERRED`

PUBLICCLOUD_LIVE: `DEFERRED`

REMOTE_MIGRATION: `PENDING`

INSTALLER: `NOT BUILT`

LIMITATIONS: this is local automated candidate evidence only. No real two-machine OnlyLAN validation, live PublicCloud validation, remote migration, installer build, production Supabase action, real-account acceptance, or production readiness claim was performed.

FINAL_VERDICT: `FINAL_CANDIDATE_LOCAL_GO`

NEXT_TASK: No further source patch task. At a later separately authorized checkpoint: apply/verify the migration in the intended Supabase environment; build the installer from this exact candidate/version; perform real two-machine OnlyLAN validation; and perform PublicCloud live validation.
