# A-05 Final Notification and Student Result Contract

## Version and provenance

- Task: `ET-A05-STANDARDIZE-NOTIFICATION-AND-RESULT-CONTRACTS`.
- Base commit: `a01664716eaae5ca38c57bedd58b63a8d3011674`.
- Shared assembly: `ExamTransfer.Shared.Contracts`.
- Contract file: `backend/src/ExamTransfer.Shared.Contracts/StudentNotificationAndResultContracts.cs`.
- Serialization: ASP.NET Core web defaults (`camelCase`) plus `JsonStringEnumConverter`, matching LocalServer controllers, SignalR JSON, and the desktop backend client.
- Enum values are JSON strings. No `Unknown` member exists. Unknown strings fail deserialization; undefined numeric values fail validation.
- Scores use `decimal`; timestamps use `DateTimeOffset` and contract validators require UTC offset `00:00`.

This task defines contracts only. It does not publish events, implement OnlyLAN or PublicCloud transport, change grading mutations, or provide the student-results API.

## Notification events

All notification payloads use `StudentNotificationEventDto` and must pass `StudentNotificationEventValidator.EnsureValid` before a producer sends them or a consumer accepts them as valid.

| EventType | Purpose | Intended scope | Required event-specific fields | Nullable/optional fields | Data that must not be included |
|---|---|---|---|---|---|
| `ParticipantApproved` | Participant admission approved | Participant | `ParticipantId` | `Message`, `Reason`, score fields | Password, token, device secret |
| `ParticipantAdmissionRejected` | Participant admission rejected | Participant | `ParticipantId` | `Reason` when supplied by the business operation | Password, token, fabricated reason |
| `TeacherMessageReceived` | Teacher message received | Session or participant | non-whitespace `Message` | `ParticipantId` for targeted messages | Broadcast decision, account secrets |
| `SubmissionRejected` | Essay/file submission rejected | Participant | `ParticipantId`, `SubmissionId` | `Reason` when supplied | `AttemptId`, physical/cloud paths |
| `ResubmitAllowed` | Essay/file resubmission allowed | Participant | `ParticipantId`, `SubmissionId` | `Message`, `Reason` | `AttemptId`, authorization tokens |
| `GradeReturned` | Essay/file grade returned | Participant | `ParticipantId`, `SubmissionId` | `Score`, `MaxScore`, `Message` | `AttemptId`, attachment path/key |
| `QuizGradeReturned` | Quiz grade returned | Participant | `ParticipantId`, `AttemptId` | `Score`, `MaxScore`, `Message` | `SubmissionId`, correct-answer content |
| `GradeReopened` | Essay/file grade reopened | Participant | `ParticipantId`, `SubmissionId` | `Reason`; old score is not required | `AttemptId`, attachment path/key |
| `QuizGradeReopened` | Quiz grade reopened | Participant | `ParticipantId`, `AttemptId` | `Reason`; old score is not required | `SubmissionId`, answer key |

Every event also requires non-empty `EventId`, non-empty `SessionId`, a non-default UTC `OccurredAtUtc`, and `Revision >= 0`.

## Notification payload schema

| Field | CLR type | Nullable | JSON name | Validation |
|---|---|---:|---|---|
| `EventId` | `Guid` | No | `eventId` | Non-empty; required JSON property |
| `EventType` | `StudentNotificationEventType` | No | `eventType` | One of the nine named values; required JSON property |
| `SessionId` | `Guid` | No | `sessionId` | Non-empty; required JSON property |
| `ParticipantId` | `Guid?` | Yes | `participantId` | Non-empty when present; required for participant-scoped events |
| `SubmissionId` | `Guid?` | Yes | `submissionId` | Essay/file identity only; non-empty when present |
| `AttemptId` | `Guid?` | Yes | `attemptId` | Quiz identity only; non-empty when present |
| `Message` | `string?` | Yes | `message` | Non-whitespace when present; required for `TeacherMessageReceived` |
| `Reason` | `string?` | Yes | `reason` | Non-whitespace when present; never synthesized by this contract |
| `Score` | `decimal?` | Yes | `score` | `>= 0` when present and `<= MaxScore` when both exist |
| `MaxScore` | `decimal?` | Yes | `maxScore` | `> 0` when present |
| `OccurredAtUtc` | `DateTimeOffset` | No | `occurredAtUtc` | Non-default and UTC offset `00:00`; required JSON property |
| `Revision` | `long` | No | `revision` | `>= 0`; required JSON property |

`SubmissionRejected`, `ResubmitAllowed`, `GradeReturned`, and `GradeReopened` reject `AttemptId`. `QuizGradeReturned` and `QuizGradeReopened` reject `SubmissionId`. This prevents one identity from silently substituting for the other.

## Student result schema

`StudentResultDto` is the shared read contract intended for A-10 and later frontend integration. It must pass `StudentResultValidator.EnsureValid`.

| Field | CLR type | Nullable | JSON name | EssayFile semantics | Quiz semantics |
|---|---|---:|---|---|---|
| `ResultType` | `StudentResultType` | No | `resultType` | `EssayFile` | `Quiz` |
| `ExamId` | `Guid` | No | `examId` | Non-empty | Non-empty |
| `ExamTitle` | `string` | No | `examTitle` | Non-whitespace | Non-whitespace |
| `SessionId` | `Guid` | No | `sessionId` | Non-empty | Non-empty |
| `SubmissionId` | `Guid?` | Yes | `submissionId` | Required and non-empty | Must be null |
| `AttemptId` | `Guid?` | Yes | `attemptId` | Must be null | Required and non-empty |
| `AttemptNumber` | `int` | No | `attemptNumber` | `> 0` | `> 0` |
| `Status` | `StudentResultStatus` | No | `status` | `Graded` or `Returned` | `Graded` or `Returned` |
| `Score` | `decimal?` | Yes | `score` | Non-negative when present | Non-negative when present |
| `MaxScore` | `decimal?` | Yes | `maxScore` | Positive when present | Positive when present |
| `GeneralComment` | `string?` | Yes | `generalComment` | Non-whitespace when present | Non-whitespace when present |
| `ReturnedAtUtc` | `DateTimeOffset?` | Yes | `returnedAtUtc` | Status invariant applies | Status invariant applies |
| `Attachments` | `IReadOnlyList<StudentResultAttachmentDto>` | No | `attachments` | Safe metadata is allowed | Defaults to an empty list when omitted |
| `QuizSummary` | `StudentQuizResultSummaryDto?` | Yes | `quizSummary` | Must be null | Summary is allowed |

`ResultType` is authoritative. Consumers must not infer EssayFile or Quiz from which identity field happens to be null.

## Result status semantics

| Status | Meaning | Student visibility | ReturnedAtUtc |
|---|---|---|---|
| `Graded` | Teacher grading is complete but has not been returned | Must not be shown by default to the student | Must be null |
| `Returned` | The result has been returned to the student | May be returned by A-10 | Required, non-default UTC |

A score does not imply `Returned`. Unknown status values are never mapped to `Returned`.

## Attachment contract

`StudentResultAttachmentDto` contains only:

| Field | Type | JSON name | Validation |
|---|---|---|---|
| `AttachmentId` | `Guid` | `attachmentId` | Non-empty |
| `FileName` | `string` | `fileName` | Non-whitespace filename metadata; path separators and rooted paths are rejected |
| `ContentType` | `string` | `contentType` | Non-whitespace |
| `SizeBytes` | `long` | `sizeBytes` | `>= 0` |

It intentionally has no physical path, `CloudObjectPath`, cache path, service key, signed URL, or download URL. A later download endpoint must authorize by identifier.

## Quiz summary

`StudentQuizResultSummaryDto` contains aggregate values with sources available from the existing grading/review data:

- `TotalQuestions`, `AnsweredQuestions`, `CorrectCount`, `IncorrectCount`, `UnansweredCount`.
- `EarnedPoints`, `MaxPoints` as `decimal`.
- Counts must be non-negative and internally consistent; points must be non-negative, `MaxPoints > 0`, and earned points cannot exceed maximum points.

The summary exposes no question text, selected options, correct options, answer key, or correct-answer content.

## Compatibility

- Existing DTOs and event contracts are retained unchanged: `RealtimeEnvelope<T>`, `RealtimeEvents`, `GradeDto`, `GradeReturnedEvent`, `QuizGradeReturnedEvent`, `StudentQuizReviewDto`, and frontend presentation models.
- The frontend references `ExamTransfer.Shared.Contracts` directly, so no mirror DTO was added to `ServiceContracts.cs`.
- The current Notification Center, realtime subscriptions, `StudentResultsService`, and Student Results UI do not consume the new DTOs in A-05.
- A-06 and A-07 should transport `StudentNotificationEventDto` and enforce `StudentNotificationEventValidator` at their contract boundary.
- A-10 should return `StudentResultDto` values and enforce `StudentResultValidator`; it must expose only `Returned` rows to student callers.
- Missing nullable notification fields continue to deserialize as null. Missing `attachments` deserializes to an empty list. Required JSON fields are marked required and missing required fields fail closed.

## Sanitized JSON examples

### TeacherMessageReceived

```json
{
  "eventId": "10000000-0000-0000-0000-000000000001",
  "eventType": "TeacherMessageReceived",
  "sessionId": "20000000-0000-0000-0000-000000000001",
  "participantId": null,
  "submissionId": null,
  "attemptId": null,
  "message": "Còn 15 phút để hoàn thành bài.",
  "reason": null,
  "score": null,
  "maxScore": null,
  "occurredAtUtc": "2026-08-02T03:04:05+00:00",
  "revision": 12
}
```

### GradeReturned

```json
{
  "eventId": "10000000-0000-0000-0000-000000000002",
  "eventType": "GradeReturned",
  "sessionId": "20000000-0000-0000-0000-000000000001",
  "participantId": "30000000-0000-0000-0000-000000000001",
  "submissionId": "40000000-0000-0000-0000-000000000001",
  "attemptId": null,
  "message": "Kết quả bài tự luận đã được trả.",
  "reason": null,
  "score": 7.5,
  "maxScore": 10,
  "occurredAtUtc": "2026-08-02T03:04:05+00:00",
  "revision": 13
}
```

### QuizGradeReturned

```json
{
  "eventId": "10000000-0000-0000-0000-000000000003",
  "eventType": "QuizGradeReturned",
  "sessionId": "20000000-0000-0000-0000-000000000001",
  "participantId": "30000000-0000-0000-0000-000000000001",
  "submissionId": null,
  "attemptId": "50000000-0000-0000-0000-000000000001",
  "message": "Kết quả trắc nghiệm đã được trả.",
  "reason": null,
  "score": 8,
  "maxScore": 10,
  "occurredAtUtc": "2026-08-02T03:04:05+00:00",
  "revision": 14
}
```

### EssayFile Returned result

```json
{
  "resultType": "EssayFile",
  "examId": "60000000-0000-0000-0000-000000000001",
  "examTitle": "Bài tự luận cuối kỳ",
  "sessionId": "20000000-0000-0000-0000-000000000001",
  "submissionId": "40000000-0000-0000-0000-000000000001",
  "attemptId": null,
  "attemptNumber": 1,
  "status": "Returned",
  "score": 7.5,
  "maxScore": 10,
  "generalComment": "Lập luận rõ ràng.",
  "returnedAtUtc": "2026-08-02T03:04:05+00:00",
  "attachments": [
    {
      "attachmentId": "70000000-0000-0000-0000-000000000001",
      "fileName": "feedback.pdf",
      "contentType": "application/pdf",
      "sizeBytes": 1234
    }
  ],
  "quizSummary": null
}
```

### Quiz Returned result

```json
{
  "resultType": "Quiz",
  "examId": "60000000-0000-0000-0000-000000000002",
  "examTitle": "Bài trắc nghiệm cuối kỳ",
  "sessionId": "20000000-0000-0000-0000-000000000001",
  "submissionId": null,
  "attemptId": "50000000-0000-0000-0000-000000000001",
  "attemptNumber": 2,
  "status": "Returned",
  "score": 8,
  "maxScore": 10,
  "generalComment": "Hoàn thành tốt.",
  "returnedAtUtc": "2026-08-02T03:04:05+00:00",
  "attachments": [],
  "quizSummary": {
    "totalQuestions": 10,
    "answeredQuestions": 9,
    "correctCount": 8,
    "incorrectCount": 1,
    "unansweredCount": 1,
    "earnedPoints": 8,
    "maxPoints": 10
  }
}
```
