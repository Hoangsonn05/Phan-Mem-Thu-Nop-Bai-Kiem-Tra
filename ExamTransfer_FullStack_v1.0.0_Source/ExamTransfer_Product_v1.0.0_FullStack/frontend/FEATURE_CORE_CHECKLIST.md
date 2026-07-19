# ExamTransfer Core Feature Matrix

Only mark `Completed` when there is real source code and a build/test path. Items that still require a real Supabase project remain `Ready for real credentials`.

| Feature | Frontend View/ViewModel | Backend endpoint | Service | Entity/table/storage | Test | Status |
|---|---|---|---|---|---|---|
| Account login | `LoginView`, `LoginViewModel`, `AppAuthSessionState` | `POST /api/v1/auth/login` | `AccountAuthenticationService` | `profiles`, `user_login_sessions` | Backend/frontend build; verify script contract check | Completed |
| Student identity confirmation | `LoginViewModel` confirmation state | `POST /api/v1/auth/student/confirm` | `LoginChallengeService`, `AccountAuthenticationService` | Challenge cache, `profiles.student_code` | Backend/frontend build; Supabase pgTAP surface check | Completed |
| Single active account session | Shell heartbeat/logout | `POST /api/v1/auth/heartbeat`, `POST /api/v1/auth/logout` | `AccountSessionService` | SQLite `user_login_sessions`; Supabase RPCs | Backend build; migration test file | Completed |
| Role-derived menu | `MainViewModel`, `MainWindow` | `GET /api/v1/auth/me` | Account auth scheme | Account claims | WPF build | Completed |
| Student room join from account identity | `StudentConnectViewModel` | `POST /api/v1/sessions/join` | `SessionService` | `SessionParticipant.UserId` | Backend/frontend build | Completed |
| Participant token split | `BackendClient`, student modules | `X-Exam-Session-Token` endpoints | Participant auth scheme | Participant token payload includes `user_id` | Backend/frontend build | Completed |
| Current exam workspace | `StudentExamViewModel`, `StudentExamView` | Session/participant heartbeat endpoints | `SessionService` | Local workspace folder | WPF build | Completed |
| Receive exam files | `StudentDownloadViewModel` | `GET /api/v1/exams/{id}/manifest`, file content | `ExamService` | Local file storage / Supabase storage projection | WPF/backend build | Completed |
| Submit files and receipt | `StudentSubmissionViewModel`, `StudentReceiptViewModel` | `init`, `chunk`, `finalize`, `receipt` | `SubmissionService` | Submission tables/storage | WPF/backend build | Completed |
| Teacher classes | `ClassManagementViewModel` | `api/v1/classes` CRUD/import/export | `ClassService` | `classes`, `class_members` | Backend/frontend build | Completed |
| Teacher exams | `ExamManagementViewModel` | `api/v1/exams` CRUD/file upload | `ExamService` | `exams`, `exam_files` | Backend/frontend build | Completed |
| Teacher sessions/lobby/monitor | `SessionManagement`, `Lobby`, `LiveMonitor` | `api/v1/sessions` | `SessionService`, SignalR | `exam_sessions`, `session_participants` | Backend/frontend build | Completed |
| Collection/export/grading/control | Product module ViewModels | `submissions`, `exports`, `grading`, `control` | Existing services | Existing tables/storage | Backend/frontend build | Completed |
| Supabase production auth | Backend services and migration | Supabase Auth + RPCs | `SupabaseIdentityClient`, RPCs | Supabase project | Requires URL/key and linked project | Ready for real credentials |
