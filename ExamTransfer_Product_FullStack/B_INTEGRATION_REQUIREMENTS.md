# Person B integration requirements

## B-06 — Student realtime notification envelope

The current frontend-normalized `StudentRealtimeNotification` contains only
`SessionId`, `EventName`, `Revision`, an optional `ParticipantId`, and payloads
for `TimeExtended` / `PublicCloudProjectionUpdated`. The notification adapter
can safely refresh authoritative state and display generic messages, but the
transport owners need to preserve the following envelope and payload fields so
the UI can provide complete event identity and content without inference:

- `EventId`: stable across retries and identical on OnlyLAN/PublicCloud.
- `EventName`: one of the existing `RealtimeEvents` contract names.
- `SessionId`.
- `ParticipantId`: explicit recipient for participant-scoped events.
- `SubmissionId`: required for `SubmissionRejected` and file-grade results.
- `ResultId`: required for returned/reopened results.
- `Reason`: required for rejection/reopen notifications.
- `Message`: required for `TeacherMessageReceived`.
- `OccurredAtUtc`.
- `Revision`.

Current publication gaps observed in the existing code:

- OnlyLAN does not publish a realtime event after `ResubmitAllowed`.
- OnlyLAN participant rejection is persisted/audited as `ParticipantRejected`
  but no participant realtime event is published.
- File-grade reopen is audited as `GradeReopened` but no realtime contract
  event is published.
- PublicCloud quiz grade signals reach the frontend as an event-name string,
  without the normalized envelope fields above.
- Generic OnlyLAN parsing drops `RealtimeEnvelope.EventId`,
  `RealtimeEnvelope.OccurredAtUtc`, and most typed payload fields.

Until the normalized contract is enriched, B-06 uses a deterministic local
dedupe key composed only of validated session, participant, revision, and the
existing event name, and records the client receive time as the popup timestamp.
It never treats realtime score/reason/message data as authoritative and does
not display draft score data.

There is currently no student file-result page or participant-authorized file
grade read endpoint in the desktop flow. `GradeReturned` therefore refreshes
the authoritative student lifecycle state and shows a generic popup; quiz
result events additionally refresh the existing quiz review page when open.

## B-09 — Returned results for the authenticated account

The results page must load a unified list for the authenticated account without
accepting or sending a caller-provided `StudentId`. The current contracts expose
only result-by-known-submission/attempt calls, so they cannot provide account
history or reliably populate all fields required by B-09.

Required authoritative contract for both OnlyLAN and PublicCloud:

- list only `Returned` results owned by the authenticated account;
- result ID, session ID, participant ID, exam title and delivery type;
- authoritative attempt number;
- score, maximum score, comment and `ReturnedAtUtc`;
- quiz question review with selected/correct choices and earned points;
- returned grade attachments with participant-authorized download URLs;
- the same semantic DTO for quiz and essay/file results across both modes.

The existing `StudentQuizReviewDto` has no grading status or `ReturnedAtUtc`.
`CorrectAnswersVisible` is temporarily used as the safe returned-result signal;
`ScoreVisible` alone is insufficient because policy may expose a score before a
teacher returns it. The existing file grade endpoint works only when a caller
already knows the current submission ID, and PublicCloud has no equivalent
essay/file result read/download contract in the desktop client.

Realtime result events must include the recipient `participant` ID and result
ID for `GradeReturned`, `QuizGradeReturned`, file/quiz reopen, and
`ResultReopened`. OnlyLAN `GradeReturned` currently omits participant identity,
and file-grade reopen has no normalized event. Without those fields the results
page cannot safely refresh for one student while rejecting another student's
event in the same session.
