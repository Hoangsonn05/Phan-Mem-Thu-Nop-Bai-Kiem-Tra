# LAN discovery, public classes, and durable submissions

## Implemented behavior

- A student scans the LAN and selects an open waiting room. The normal screen does not ask for an IP address, port, or URL.
- Discovery exposes only open `LanOnly` sessions and is denied outside loopback, the actual local subnet, or an explicitly configured private CIDR.
- LAN participant traffic is rejected when the remote address is not on an allowed local subnet. Internet-routable client addresses are never accepted by the LAN allowlist.
- A student submission is exactly one `.zip`, `.rar`, or `.7z` file, from 1 byte through 10 MiB. Both extension and archive magic are checked. This fixed rule is separate from teacher exam-file rules.
- Submission preparation copies the selected archive into an application-owned spool, flushes it durably, hashes the spool copy, and then records an idempotent queue item. Upload resumes only missing chunks. Queue and spool cleanup happen only after a receipt is stored durably.
- The recovery worker starts with the desktop application, survives navigation away from the submission screen, retries with bounded backoff, reacts to network restoration, re-discovers LAN rooms, and can use a new participant token after the student rejoins.
- Classes and sessions have explicit `Private`/`Public` and `LanOnly`/`PublicCloud` modes. A `PublicCloud` session can only be created for a public class.
- The Supabase migration adds public enrollment, exam assignment, device connection, command/result, submission, Storage, and private Realtime authorization. Enrollment codes are bcrypt hashes and are scoped to the authenticated student's organization.
- Public device commands are target-bound, signed, expiring, and idempotent. A persisted policy lease causes the agent to remove an expired policy. The existing capability-only agent remains non-invasive.

## Firewall policy

Installer and setup scripts create TCP/UDP rules for the Private profile with `LocalSubnet` remote scope. The installer warns that the teacher's network must be set to Private.

## Acceptance commands

Run from the product root:

```powershell
./backend/scripts/test-lan-room-discovery.ps1
./backend/scripts/test-student-submission-policy.ps1
./backend/scripts/test-submission-recovery.ps1
./backend/scripts/test-public-class-workflow.ps1
./backend/scripts/test-public-device-control.ps1
dotnet build ./ExamTransfer.slnx -c Release
dotnet test ./backend/tests/ExamTransfer.Infrastructure.Tests/ExamTransfer.Infrastructure.Tests.csproj -c Release
```

For a live LAN discovery check, pass the teacher endpoint only to the acceptance script (not through the student UI):

```powershell
./backend/scripts/test-lan-room-discovery.ps1 -BaseUri http://teacher-host:5049
```

For database verification, start Supabase locally and run:

```powershell
npx --no-install supabase db reset --workdir backend
npx --no-install supabase test db --workdir backend
```

The pgTAP suite includes `0004_public_classes_device_control.sql`. Do not report the database path as verified unless those commands complete successfully.

## Deployment boundary

The migration is source code only until it is applied to the intended Supabase project. A live Internet workflow also requires configured Supabase URL/publishable key, authenticated users, a private Realtime channel subscription, and a deployed command transport. Never place a service-role or secret key in the desktop application.
