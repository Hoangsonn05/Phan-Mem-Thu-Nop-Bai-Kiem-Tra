# PublicCloud ownership and projection

| Entity | Primary source | Allowed writers | Sync direction | SQLite target | Consumer |
|---|---|---|---|---|---|
| `class_enrollment_requests` | Supabase | Student join RPC; authorized teacher enrollment RPC | Supabase → pull | `class_enrollment_requests` | Class service and Class Management UI |
| `class_members` | Supabase for approved public enrollment; SQLite for LAN roster | Enrollment trigger or LAN class service | Source-dependent; no reverse push for `PublicCloud` rows | `class_members` | Class service and student roster |
| `session_participants` | Supabase in PublicCloud | Student join RPC and authorized teacher RPCs | Supabase → pull | `session_participants` | Session service and monitoring UI |
| `submissions` | Supabase in PublicCloud | Student submission RPCs and authorized reject RPC | Supabase → pull | `submissions` | Submission/grading services |
| `submission_files` | Supabase in PublicCloud | Submission/verification RPC flow | Supabase → pull | `submission_files` | Submission and download services |
| `public_device_connections` | Supabase | Authenticated student heartbeat RPC | Supabase → pull | `public_device_connections` | Control service and Control Center UI |
| `public_device_commands` | Supabase | Authorized staff command flow | Supabase → pull | `public_device_commands` | Control service |
| `public_device_command_results` | Supabase | Authenticated device acknowledgement RPC | Supabase → pull | `public_device_command_results` | Control service and Control Center UI |
| `violations` | Supabase in PublicCloud | Authenticated device/student telemetry flow | Supabase → pull | `violations` | Control service and violation UI |
| `quiz_attempts` | Supabase in PublicCloud | Student quiz RPCs | Supabase → pull | `quiz_attempts` | Quiz service and teacher monitoring API |
| `quiz_answers` | Supabase in PublicCloud | Student answer RPC with monotonic revision | Supabase → pull | `quiz_answers` | Quiz service and teacher monitoring API |
| `grades` | SQLite/local server | Teacher grading service | SQLite → cloud projection | `grades` | Grading and reporting services |

Raw replica rows remain available for audit and diagnostics. They are not the
sole read model for any entity listed above. Pull projection rows are stamped
`SourceMode = PublicCloud` with their cloud cursor metadata and never enqueue a
generic outbox item.
