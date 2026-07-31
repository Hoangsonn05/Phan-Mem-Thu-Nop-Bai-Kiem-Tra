RESULT: PASS
TASK: ET-RUNTIME-FINAL-CANDIDATE-SCRIPT-R1
MODEL/CAPABILITY/REASONING: Codex (GPT-5) / C2 / R2 High

HEAD: 724f34da4554c8af812b897fdb55e5f3304ad315
WORKTREE_BEFORE: Tracked worktree clean. Four allowed untracked reports only: ET-RUNTIME-REBASE-03A_REPORT.md, ET-RUNTIME-STABILIZE-03E2-DESIGN-01_REPORT.md, ET-RUNTIME-STABILIZE-03E2-R1_REPORT.md, ET-RUNTIME-FINAL-INTEGRATION-04A_REPORT.md.
WORKTREE_AFTER: Expected task changes only: two modified release/E2E scripts, two new script/contract-test files, and this untracked report; the four pre-existing untracked reports remain untouched.

ROOT_CAUSES_FIXED:
- Candidate workflow previously asserted clean identity without checking tracked Git state.
- Cleanup used an unresolved relative candidate path.
- Candidate published only the backend and retained stale task provenance.
- Published OnlyLAN E2E was not invoked or failure-propagated.
- No independent post-E2E release consistency gate verified HEAD, BuildId, runtime identity, artifacts, provenance, or E2E result.
FILES_CREATED:
- scripts/release-consistency.ps1
- scripts/test-final-candidate-contract.ps1
- ET-RUNTIME-FINAL-CANDIDATE-SCRIPT-R1_REPORT.md
FILES_CHANGED:
- scripts/build-onlylan-published-candidate.ps1
- scripts/test-published-onlylan-e2e.ps1

TRACKED_WORKTREE_CHECK: Uses `git status --porcelain=v1 --untracked-files=no` against the resolved Git root before path cleanup, restore, build, publish, or manifest creation. Blocking output includes every tracked porcelain entry.
UNTRACKED_FILE_BEHAVIOR: Untracked files, including reports, are ignored by the candidate cleanliness gate and are not packaged by the explicit Client/Server publish outputs.
DIRTY_BUILD_BEHAVIOR: Emits `RESULT: BLOCKED` and `REASON: TRACKED_WORKTREE_NOT_CLEAN`, then throws before any destructive/build action.
MANIFEST_WORKTREE_VALUE: Boolean `workingTreeDirty=false` is written only after the real tracked-clean gate passes; both publishes receive `ExamTransferWorkingTreeDirty=false` only after that gate.

OUTPUT_ROOT: `<project>\artifacts\onlylan-published-e2e\candidate` by default; an override must remain a strict descendant of `<project>\artifacts\onlylan-published-e2e`.
OUTPUT_CANONICALIZATION: Project, artifacts, candidate, scratch, and drive paths are converted to absolute canonical paths before cleanup; existing candidate ancestors are rejected when they are reparse points.
CONTAINMENT_RULE: Reject project root, artifacts root, candidate-artifacts root, drive root, traversal outside the allowed root, and all other outside paths.
UNSAFE_PATH_TESTS: PASS for repository root, artifacts root, drive root, `..` escape, and an absolute outside path; a valid child path passes.
REMOVE_ITEM_SAFETY: Only the exact validated candidate root is recursively removed. Smoke output is inside that root. Harness sentinel remained intact for all rejected paths.

BUILD_ID_GENERATION: One BuildId assignment from Version + eight-character Git HEAD + one UTC build-start timestamp.
BUILD_ID_PROPAGATION: The same `$buildId` is passed to frontend publish, backend publish, manifest, runtime health comparison, Published E2E through the manifest, and release consistency.
BUILD_ID_REGENERATION: None; static test found exactly one BuildId assignment and exactly two publish-property uses.

INTEGRATION_GATE_METADATA: ET-RUNTIME-FINAL-INTEGRATION-04A
CANDIDATE_TASK_METADATA: ET-RUNTIME-FINAL-CANDIDATE-04B
OLD_METADATA_REMOVED: ET-LAN-MODULE-REFACTOR-01D and ET-LAN-PUBLISHED-CANDIDATE-BUILD-ID-ATOMIC-01 are absent from the candidate script.
MANIFEST_PROVENANCE: Includes GitHead/GitCommit, Version/SemanticVersion, BuildId, boolean WorkingTreeDirty, IntegrationGate, CandidateTask, ordered patch chain 03C/03D-AUTHORITY-01/03D-R1/03E1/03E2-R1, BuildStartUtc/BuildFinishUtc, RID, self-contained flag, frontend/backend files and hashes, and Published E2E state.

PUBLISHED_E2E_SCRIPT: scripts/test-published-onlylan-e2e.ps1
PUBLISHED_E2E_CALL_ORDER: Exactly once after both publishes and runtime health/BuildId verification, before manifest finalization, consistency, and final PASS.
PUBLISHED_E2E_PARAMETERS: Receives current candidate ServerDirectory, backward-compatible optional ClientDirectory, and bounded Repeat. ClientDirectory validation proves the frontend executable is from the same candidate root and matches the manifest SHA-256.
PUBLISHED_E2E_FAILURE_PROPAGATION: Non-zero exit emits `RESULT: FAIL` / `REASON: PUBLISHED_ONLYLAN_E2E_FAILED` and throws; manifest remains PENDING/not-run.
HEALTH_ONLY_ACCEPTED: No. Health is a prerequisite; only a real E2E zero exit changes manifest to `publishedE2ERan=true` and `publishedE2EResult=PASS`.

CONSISTENCY_SCRIPT: scripts/release-consistency.ps1; read-only over the manifest and artifacts.
CONSISTENCY_HEAD: Verifies actual repository HEAD, requested HEAD, and manifest GitHead match.
CONSISTENCY_BUILD_ID: Verifies requested BuildId, manifest BuildId, and runtime-health BuildId match.
CONSISTENCY_HASHES: Verifies contained frontend/backend paths, file existence, SHA-256, size, optional file/product versions, and main-file timestamps inside the build window.
CONSISTENCY_WORKTREE: Re-runs tracked-only Git cleanliness and requires manifest boolean `workingTreeDirty=false`.
CONSISTENCY_PROVENANCE: Requires the exact final integration gate, candidate task, ordered patch chain, RID `win-x64`, self-contained true, and a valid build window.
CONSISTENCY_E2E: Requires boolean ran=true and result=PASS; mismatch produces `RELEASE_CONSISTENCY: FAIL`. The valid-fixture test also proves the script does not mutate the manifest.

SCRIPT_PARSE_RESULTS: PASS, 4/4 modified/new PowerShell scripts.
FOCUSED_SCRIPT_TESTS: PASS, deterministic isolated harness 32/32 assertions. It used temporary Git repositories and fake artifacts; it did not publish or execute the application.
STATIC_CONTRACT_RESULTS: PASS for one BuildId, two publish propagations, one E2E call, mandatory ordering, real Git HEAD, current provenance, safe cleanup ordering, current candidate directories, and failure propagation.
CANDIDATE_BUILD_RUN: NOT RUN (explicitly forbidden in this task).
PUBLISHED_E2E_RUN: NOT RUN (explicitly forbidden in this task).
DIFF_CHECK: PASS (`git diff --check`); new script trailing-whitespace check also PASS.

PRODUCTION_FILES_CHANGED: None under frontend/src or backend/src.
TEST_BUSINESS_FILES_CHANGED: None under frontend/tests or backend/tests.
SUPABASE_IMPACT: None; no migration, RPC, schema, RLS, configuration, or production Supabase operation.

FINAL_VERDICT:
CANDIDATE_SCRIPT_CONTRACT_PASS

NEXT_TASK:
CHECKPOINT ET-RUNTIME-FINAL-CANDIDATE-SCRIPT-R1
-> commit separately
-> rerun ET-RUNTIME-FINAL-INTEGRATION-04A
-> do not start 04B until 04A returns FINAL_INTEGRATION_GO
