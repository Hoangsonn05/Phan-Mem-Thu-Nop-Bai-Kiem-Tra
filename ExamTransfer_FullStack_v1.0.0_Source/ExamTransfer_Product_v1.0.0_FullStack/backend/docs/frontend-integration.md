# Frontend Integration Contract

## Default addresses

- REST base URL: `http://{teacher-ip}:5048/api/v1`
- SignalR hub: `http://{teacher-ip}:5048/hubs/exam`
- UDP discovery port: `5050`

## Authentication

- Application account token: `Authorization: Bearer <account-token>`.
- Exam participant token: `X-Exam-Session-Token: <participant-token>`.
- The frontend must call `POST /api/v1/auth/login` first. Student accounts complete `POST /api/v1/auth/student/confirm` before receiving an account token.
- `POST /api/v1/sessions/join` requires an authenticated Student account. The backend derives student code and display name from the account profile.
- Participant-scoped room, exam-file and submission calls use only the participant token header after join.
- Development teacher tokens are disabled by default and require `Auth:AllowDevelopmentToken=true` in Development/Test.

## Contract rules

- JSON enums use names from `ExamTransfer.Shared.Contracts`.
- IDs are UUIDs.
- Business timestamps use UTC `DateTimeOffset`; the frontend localizes only for display.
- Responses use `ApiResponse<T>`, `ApiError.Code`, `fieldErrors`, `traceId`, and `schemaVersion`.
