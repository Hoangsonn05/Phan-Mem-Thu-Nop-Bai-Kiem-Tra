using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Security;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.Infrastructure.Storage;
using ExamTransfer.LocalServer.Workers;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class PublicCloudTeacherMutationTests
{
    [Fact]
    public void Migration_exposes_narrow_security_definer_rpcs_with_authenticated_only_grants()
    {
        var sql = PublicCloudTestHarness.ReadRepositoryFile(
            "backend/supabase/migrations/20260723043859_public_cloud_teacher_mutations_and_projection.sql");
        foreach (var rpc in new[]
        {
            "approve_public_participant", "reject_public_participant",
            "bulk_approve_public_participants", "add_public_participant_extra_time",
            "allow_public_resubmission", "reject_public_submission",
            "approve_public_enrollment_request", "reject_public_enrollment_request"
        })
        {
            Assert.Contains($"function public.{rpc}", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"grant execute on function public.{rpc}", sql, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("security definer", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("set search_path = ''", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("private.begin_public_teacher_mutation", sql, StringComparison.Ordinal);
        Assert.Contains("private.write_public_teacher_audit", sql, StringComparison.Ordinal);
    }
}

public sealed class PublicCloudTeacherMutationRoutingTests
{
    [Fact]
    public async Task Approve_PublicCloud_calls_rpc_without_mutating_sqlite_or_creating_outbox()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var participant = await PublicCloudTestHarness.SeedSessionAsync(database.Context, SessionAccessMode.PublicCloud);
        var cloud = new RecordingCloudAdapter();
        var service = PublicCloudTestHarness.CreateSessionService(database.Context, cloud);
        var mutationRequestId = Guid.NewGuid();

        var result = await service.ApproveAsync(participant.SessionId, participant.Id, mutationRequestId, CancellationToken.None);

        Assert.Equal(1, cloud.ApproveCalls);
        Assert.Equal(mutationRequestId, cloud.LastApproveRequestId);
        Assert.Equal(ParticipantStatus.Approved, result.Status);
        database.Context.ChangeTracker.Clear();
        Assert.Equal(
            ParticipantStatus.PendingApproval,
            (await database.Context.SessionParticipantsSet.SingleAsync(x => x.Id == participant.Id)).Status);
        Assert.Empty(await database.Context.SyncQueueSet.ToListAsync());
    }

    [Fact]
    public async Task Failed_PublicCloud_rpc_does_not_fake_approval_in_sqlite()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var participant = await PublicCloudTestHarness.SeedSessionAsync(database.Context, SessionAccessMode.PublicCloud);
        var cloud = new RecordingCloudAdapter { FailApprove = true };
        var service = PublicCloudTestHarness.CreateSessionService(database.Context, cloud);
        var mutationRequestId = Guid.NewGuid();

        await Assert.ThrowsAsync<ApiException>(
            () => service.ApproveAsync(participant.SessionId, participant.Id, mutationRequestId, CancellationToken.None));
        Assert.Equal(mutationRequestId, cloud.LastApproveRequestId);

        database.Context.ChangeTracker.Clear();
        Assert.Equal(
            ParticipantStatus.PendingApproval,
            (await database.Context.SessionParticipantsSet.SingleAsync(x => x.Id == participant.Id)).Status);
        Assert.Empty(await database.Context.SyncQueueSet.ToListAsync());
    }

    [Fact]
    public async Task LanOnly_approval_keeps_existing_sqlite_and_outbox_flow()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var participant = await PublicCloudTestHarness.SeedSessionAsync(database.Context, SessionAccessMode.LanOnly);
        var cloud = new RecordingCloudAdapter { FailApprove = true };
        var service = PublicCloudTestHarness.CreateSessionService(database.Context, cloud);

        await service.ApproveAsync(participant.SessionId, participant.Id, Guid.Empty, CancellationToken.None);

        Assert.Equal(0, cloud.ApproveCalls);
        Assert.Equal(
            ParticipantStatus.Approved,
            (await database.Context.SessionParticipantsSet.SingleAsync(x => x.Id == participant.Id)).Status);
        Assert.Contains(
            await database.Context.SyncQueueSet.ToListAsync(),
            x => x.EntityType == "session_participants" && x.EntityId == participant.Id.ToString());
    }

    [Fact]
    public async Task PublicCloud_resubmit_and_submission_reject_use_rpcs_without_local_final_state()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var participant = await PublicCloudTestHarness.SeedSessionAsync(database.Context, SessionAccessMode.PublicCloud);
        participant.SubmissionStatus = SubmissionStatus.Submitted;
        var submission = new Submission
        {
            Id = Guid.NewGuid(), Participant = participant, SessionId = participant.SessionId,
            AttemptNumber = 1, IdempotencyKey = "public-routing-test",
            Status = SubmissionStatus.Submitted, ClientSubmittedAtUtc = DateTimeOffset.UtcNow,
            DeadlineUtc = DateTimeOffset.UtcNow.AddHours(1), SourceMode = "PublicCloud"
        };
        database.Context.SubmissionsSet.Add(submission);
        await database.Context.SaveChangesAsync();
        var cloud = new RecordingCloudAdapter();
        var options = Options.Create(new ExamTransferOptions());
        var service = new SubmissionService(
            database.Context,
            new TestStoragePaths(),
            new ChunkStorage(),
            new ReceiptSigner(options),
            new AuditService(database.Context, new HttpContextAccessor()),
            new OutboxService(database.Context),
            new TestRealtimePublisher(),
            options,
            cloud);

        var resubmitRequestId = Guid.NewGuid();
        var rejectRequestId = Guid.NewGuid();
        await service.AllowResubmitAsync(participant.Id, new("Approved retry", resubmitRequestId), CancellationToken.None);
        await service.RejectAsync(submission.Id, new("Unreadable archive", rejectRequestId), CancellationToken.None);

        Assert.Equal(1, cloud.ResubmitCalls);
        Assert.Equal(1, cloud.RejectSubmissionCalls);
        Assert.Equal(resubmitRequestId, cloud.LastResubmitRequestId);
        Assert.Equal(rejectRequestId, cloud.LastRejectSubmissionRequestId);
        database.Context.ChangeTracker.Clear();
        Assert.False((await database.Context.SessionParticipantsSet.SingleAsync(x => x.Id == participant.Id)).ResubmitAllowed);
        Assert.Equal(
            SubmissionStatus.Submitted,
            (await database.Context.SubmissionsSet.SingleAsync(x => x.Id == submission.Id)).Status);
        Assert.Empty(await database.Context.SyncQueueSet.ToListAsync());
    }

    private sealed class TestRealtimePublisher : IRealtimePublisher
    {
        public Task PublishSessionAsync<T>(Guid sessionId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishParticipantAsync<T>(Guid sessionId, Guid participantId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestStoragePaths : IStoragePaths
    {
        public string RootPath { get; } = Path.Combine(Path.GetTempPath(), "ExamTransfer.PublicCloud.Storage");
        public string DatabasePath => Path.Combine(RootPath, "database.db");
        public string BackupRoot => Path.Combine(RootPath, "backups");
        public string ExportRoot => Path.Combine(RootPath, "exports");
        public string TemporaryRoot => Path.Combine(RootPath, "temp");
        public string ExamVersionRoot(Guid examId, int version) => Path.Combine(RootPath, "exams", examId.ToString("N"), version.ToString());
        public string SessionRoot(Guid sessionId) => Path.Combine(RootPath, "sessions", sessionId.ToString("N"));
        public string SubmissionRoot(Guid sessionId, string studentCode, Guid submissionId) => Path.Combine(SessionRoot(sessionId), studentCode, submissionId.ToString("N"));
        public string ReceiptRoot(Guid sessionId) => Path.Combine(SessionRoot(sessionId), "receipts");
        public void EnsureCreated() => Directory.CreateDirectory(RootPath);
    }
}

public sealed class PublicCloudPullProjectionTests
{
    [Fact]
    public async Task Pull_projects_enrollment_and_approved_participant_into_business_tables()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var participant = await PublicCloudTestHarness.SeedSessionAsync(database.Context, SessionAccessMode.PublicCloud);
        var classroom = new ClassRoom
        {
            Id = Guid.NewGuid(), Name = "Public class", Code = "PUB", SchoolYear = "2026-2027",
            AccessMode = ClassAccessMode.Public
        };
        database.Context.ClassesSet.Add(classroom);
        await database.Context.SaveChangesAsync();
        var enrollmentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var cloud = new PullCloudAdapter(new Dictionary<string, CloudPullRecord>
        {
            ["class_enrollment_requests"] = new(
                "class_enrollment_requests", enrollmentId.ToString(), 10, now,
                JsonSerializer.Serialize(new
                {
                    id = enrollmentId, class_id = classroom.Id, student_user_id = Guid.NewGuid(),
                    student_code = "SV-PUB", status = "Pending", requested_at = now,
                    decided_at = (DateTimeOffset?)null, decided_by = (Guid?)null,
                    decision_reason = (string?)null, updated_at = now, cloud_version = 10
                })),
            ["session_participants"] = new(
                "session_participants", participant.Id.ToString(), 11, now.AddSeconds(1),
                JsonSerializer.Serialize(new
                {
                    id = participant.Id, session_id = participant.SessionId, user_id = Guid.NewGuid(),
                    student_code = participant.StudentCode, display_name = participant.DisplayName,
                    device_id = participant.DeviceId, machine_name = participant.MachineName,
                    app_version = participant.AppVersion, status = "Approved", joined_at = now,
                    approved_at = now, last_seen_at = now, download_status = "NotStarted",
                    submission_status = "NotStarted", extra_time_minutes = 0,
                    resubmit_allowed = false, updated_at = now, cloud_version = 11
                }))
        });

        await PublicCloudTestHarness.RunPullOnceAsync(database.Path, cloud);
        await PublicCloudTestHarness.RunPullOnceAsync(database.Path, cloud);

        await using var verify = database.CreateContext();
        var classService = new ClassService(
            verify,
            new MemoryCache(new MemoryCacheOptions()),
            new AuditService(verify, new HttpContextAccessor()),
            new OutboxService(verify));
        var classDetail = await classService.GetAsync(classroom.Id, CancellationToken.None);
        Assert.Equal("Pending", Assert.Single(classDetail.EnrollmentRequests!).Status);
        var projected = await verify.SessionParticipantsSet.SingleAsync(x => x.Id == participant.Id);
        Assert.Equal(ParticipantStatus.Approved, projected.Status);
        Assert.Equal("PublicCloud", projected.SourceMode);
        Assert.Equal(11, projected.CloudVersion);
        Assert.Single(await verify.PublicCloudReplicaRecordsSet.Where(
            x => x.EntityName == "session_participants" && x.CloudEntityId == participant.Id.ToString()).ToListAsync());
        Assert.Equal(
            11,
            (await verify.PublicCloudPullCursorsSet.SingleAsync(x => x.EntityName == "session_participants")).LastCloudVersion);
    }

    [Fact]
    public async Task Pull_connects_member_device_command_and_quiz_projections_to_services()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var participant = await PublicCloudTestHarness.SeedSessionAsync(database.Context, SessionAccessMode.PublicCloud);
        var userId = Guid.NewGuid();
        var classroom = new ClassRoom
        {
            Id = Guid.NewGuid(), Name = "Projection class", Code = "PROJ", SchoolYear = "2026-2027",
            AccessMode = ClassAccessMode.Public
        };
        var existingMember = new ClassMember
        {
            Id = Guid.NewGuid(), Class = classroom, UserId = userId,
            StudentCode = "SV-PROJ", DisplayName = "Existing member"
        };
        var question = new QuizQuestion
        {
            Id = Guid.NewGuid(), ExamId = participant.Session.ExamId, Version = 1,
            Order = 1, Text = "Projected question", Points = 1
        };
        database.Context.ClassMembersSet.Add(existingMember);
        database.Context.QuizQuestionsSet.Add(question);
        await database.Context.SaveChangesAsync();

        var cloudMemberId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var answerId = Guid.NewGuid();
        var choiceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var records = new Dictionary<string, CloudPullRecord>
        {
            ["class_members"] = new("class_members", cloudMemberId.ToString(), 20, now,
                JsonSerializer.Serialize(new
                {
                    id = cloudMemberId, class_id = classroom.Id, user_id = userId,
                    student_code = "SV-PROJ", display_name = "Cloud member",
                    email = "student@example.test", metadata_json = new { source = "enrollment" }
                })),
            ["public_device_connections"] = new("public_device_connections", connectionId.ToString(), 21, now.AddSeconds(1),
                JsonSerializer.Serialize(new
                {
                    id = connectionId, session_id = participant.SessionId, participant_id = participant.Id,
                    user_id = userId, device_id = "device-projected", connection_state = "Online",
                    heartbeat_at = now, policy_state = "Applied", lock_state = "Locked",
                    violation_count = 2, app_version = "2.0", agent_version = "3.0"
                })),
            ["public_device_commands"] = new("public_device_commands", commandId.ToString(), 22, now.AddSeconds(2),
                JsonSerializer.Serialize(new
                {
                    command_id = commandId, session_id = participant.SessionId, device_id = "device-projected",
                    command_type = "LockExamApplication", payload = new { }, created_at = now,
                    expires_at = now.AddMinutes(5), issued_by = Guid.NewGuid(),
                    signature = new string('a', 64), retry_count = 0
                })),
            ["public_device_command_results"] = new("public_device_command_results", commandId.ToString(), 23, now.AddSeconds(3),
                JsonSerializer.Serialize(new
                {
                    command_id = commandId, device_id = "device-projected", status = "Failed",
                    received_at = now, executed_at = now, error_code = "AGENT_ERROR",
                    error_message = "Command failed in acceptance fixture"
                })),
            ["quiz_attempts"] = new("quiz_attempts", attemptId.ToString(), 24, now.AddSeconds(4),
                JsonSerializer.Serialize(new
                {
                    id = attemptId, session_id = participant.SessionId, participant_id = participant.Id,
                    exam_version = 1, status = "InProgress", started_at = now,
                    deadline_at = now.AddHours(1), finalized_at = (DateTimeOffset?)null,
                    score = (decimal?)null, max_score = 1,
                    snapshot_json = new[]
                    {
                        new
                        {
                            id = question.Id, sortOrder = 1, questionText = "Projected question",
                            points = 1, multiple = false,
                            choices = new[] { new { id = choiceId, sortOrder = 1, choiceText = "Visible choice" } }
                        }
                    },
                    finalize_idempotency_key = (string?)null
                })),
            ["quiz_answers"] = new("quiz_answers", answerId.ToString(), 25, now.AddSeconds(5),
                JsonSerializer.Serialize(new
                {
                    id = answerId, attempt_id = attemptId, question_id = question.Id,
                    choice_ids = new[] { choiceId }, revision = 3, client_updated_at = now
                }))
        };

        await PublicCloudTestHarness.RunPullOnceAsync(database.Path, new PullCloudAdapter(records));

        await using var verify = database.CreateContext();
        Assert.Single(await verify.ClassMembersSet.Where(x => x.ClassId == classroom.Id && x.UserId == userId).ToListAsync());
        Assert.Equal(
            existingMember.Id,
            (await verify.PublicCloudIdMappingsSet.SingleAsync(
                x => x.EntityName == "class_members" && x.CloudEntityId == cloudMemberId.ToString())).LocalEntityId);

        var control = new ControlService(
            verify,
            new AuditService(verify, new HttpContextAccessor()),
            new TestRealtimePublisher(),
            new OutboxService(verify));
        var device = Assert.Single(await control.GetDeviceStatusAsync(participant.SessionId, CancellationToken.None));
        Assert.Equal(ConnectionState.Online, device.ConnectionState);
        Assert.Equal("Locked", device.LockState);
        Assert.Equal("3.0", device.AgentVersion);
        Assert.Equal(DeviceCommandStatus.Failed, device.LastCommandStatus);
        Assert.Equal("Command failed in acceptance fixture", device.LastCommandError);

        var attempts = await new QuizService(verify, new OutboxService(verify))
            .ListAttemptsForSessionAsync(participant.SessionId, CancellationToken.None);
        var attempt = Assert.Single(attempts);
        Assert.Equal(attemptId, attempt.Id);
        Assert.Equal(3, Assert.Single(attempt.Answers).Revision);
        var projectedQuestion = Assert.Single(attempt.Questions);
        Assert.Equal("Projected question", projectedQuestion.Text);
        Assert.Equal("Visible choice", Assert.Single(projectedQuestion.Choices).Text);

        var finalized = await verify.QuizAttemptsSet.SingleAsync(x => x.Id == attemptId);
        finalized.Status = QuizAttemptStatus.Finalized;
        finalized.FinalizedAtUtc = now;
        finalized.Score = 1;
        await verify.SaveChangesAsync();
        await PublicCloudTestHarness.RunPullOnceAsync(database.Path, new PullCloudAdapter(
            new Dictionary<string, CloudPullRecord>
            {
                ["quiz_attempts"] = new("quiz_attempts", attemptId.ToString(), 30, now.AddMinutes(1),
                    JsonSerializer.Serialize(new
                    {
                        id = attemptId, session_id = participant.SessionId, participant_id = participant.Id,
                        exam_version = 1, status = "InProgress", started_at = now,
                        deadline_at = now.AddHours(2), score = (decimal?)null, max_score = 1,
                        snapshot_json = Array.Empty<object>()
                    }))
            }));
        verify.ChangeTracker.Clear();
        var protectedAttempt = await verify.QuizAttemptsSet.SingleAsync(x => x.Id == attemptId);
        Assert.Equal(QuizAttemptStatus.Finalized, protectedAttempt.Status);
        Assert.Equal(1, protectedAttempt.Score);
    }

    private sealed class TestRealtimePublisher : IRealtimePublisher
    {
        public Task PublishSessionAsync<T>(Guid sessionId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishParticipantAsync<T>(Guid sessionId, Guid participantId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

public sealed class PublicCloudOutboxLoopPreventionTests
{
    [Fact]
    public async Task Pulled_projection_does_not_create_reverse_sync_queue_item()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var participant = await PublicCloudTestHarness.SeedSessionAsync(database.Context, SessionAccessMode.PublicCloud);
        var now = DateTimeOffset.UtcNow;
        var cloud = new PullCloudAdapter(new Dictionary<string, CloudPullRecord>
        {
            ["session_participants"] = new(
                "session_participants", participant.Id.ToString(), 7, now,
                JsonSerializer.Serialize(new
                {
                    id = participant.Id, session_id = participant.SessionId,
                    student_code = participant.StudentCode, display_name = participant.DisplayName,
                    device_id = participant.DeviceId, machine_name = participant.MachineName,
                    app_version = participant.AppVersion, status = "Approved", joined_at = now,
                    download_status = "NotStarted", submission_status = "NotStarted",
                    extra_time_minutes = 0, resubmit_allowed = false,
                    updated_at = now, cloud_version = 7
                }))
        });

        await PublicCloudTestHarness.RunPullOnceAsync(database.Path, cloud);

        await using var verify = database.CreateContext();
        Assert.Empty(await verify.SyncQueueSet.ToListAsync());
    }
}

public sealed class PublicCloudCursorTransactionTests
{
    [Fact]
    public async Task Projection_failure_rolls_back_replica_and_does_not_advance_cursor()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var badId = Guid.NewGuid();
        var cloud = new PullCloudAdapter(new Dictionary<string, CloudPullRecord>
        {
            ["class_members"] = new(
                "class_members", badId.ToString(), 99, DateTimeOffset.UtcNow,
                """{"student_code":"missing-required-class-id","display_name":"Broken"}""")
        });

        await PublicCloudTestHarness.RunPullOnceAsync(database.Path, cloud);

        await using var verify = database.CreateContext();
        Assert.False(await verify.PublicCloudReplicaRecordsSet.AnyAsync(
            x => x.EntityName == "class_members" && x.CloudEntityId == badId.ToString()));
        var cursor = await verify.PublicCloudPullCursorsSet
            .SingleOrDefaultAsync(x => x.EntityName == "class_members");
        Assert.True(cursor is null || cursor.LastCloudVersion == 0);
        Assert.Contains(
            await verify.PublicCloudPullFailuresSet.ToListAsync(),
            x => x.EntityName == "class_members" && x.ResolvedAtUtc == null);
    }
}

public sealed class PublicCloudMigrationSafetyTests
{
    [Fact]
    public void Compatibility_migration_uses_partial_indexes_and_preflight_guards_optional_columns()
    {
        var migration = PublicCloudTestHarness.ReadRepositoryFile(
            "backend/supabase/migrations/20260723043859_public_cloud_teacher_mutations_and_projection.sql");
        var preflight = PublicCloudTestHarness.ReadRepositoryFile(
            "backend/supabase/preflight/public_cloud_production_legacy_preflight.sql");

        Assert.Contains("where source_mode = 'PublicCloud'", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("drop index if exists public.ux_submission_files_submission", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("if new.source_mode <> 'PublicCloud' then return new", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("information_schema.columns", preflight, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("execute $sql$", preflight, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BLOCKER|", preflight, StringComparison.Ordinal);
        Assert.DoesNotContain("delete from", preflight, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("update public.", preflight, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class PublicCloudTestHarness
{
    public static async Task<TestDatabase> CreateDatabaseAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ExamTransfer.PublicCloud.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "public-cloud.db");
        var context = CreateContext(path);
        await context.Database.EnsureCreatedAsync();
        return new TestDatabase(directory, path, context);
    }

    public static async Task<SessionParticipant> SeedSessionAsync(AppDbContext db, SessionAccessMode accessMode)
    {
        var exam = new Exam
        {
            Id = Guid.NewGuid(), Title = "PublicCloud", Subject = "Test", DurationMinutes = 60,
            Status = ExamStatus.Published
        };
        var session = new ExamSession
        {
            Id = Guid.NewGuid(), Exam = exam, RoomCode = Guid.NewGuid().ToString("N")[..8],
            Status = SessionStatus.Waiting, HostDeviceId = "host", AccessMode = accessMode
        };
        var participant = new SessionParticipant
        {
            Id = Guid.NewGuid(), Session = session, StudentCode = "SV001", DisplayName = "Student",
            DeviceId = "device-1", MachineName = "machine", AppVersion = "1.0",
            Status = ParticipantStatus.PendingApproval,
            SourceMode = accessMode == SessionAccessMode.PublicCloud ? "PublicCloud" : "Lan"
        };
        db.SessionParticipantsSet.Add(participant);
        await db.SaveChangesAsync();
        return participant;
    }

    public static SessionService CreateSessionService(AppDbContext db, ICloudAdapter cloud)
    {
        var options = Options.Create(new ExamTransferOptions());
        return new SessionService(
            db,
            new SessionTokenService(options),
            new AuditService(db, new HttpContextAccessor()),
            new OutboxService(db),
            new NoOpRealtimePublisher(),
            options,
            NullLogger<SessionService>.Instance,
            cloud: cloud);
    }

    public static async Task RunPullOnceAsync(string databasePath, ICloudAdapter cloud)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton(cloud);
        await using var provider = services.BuildServiceProvider();
        var worker = new PublicCloudPullWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PublicCloudPullWorker>.Instance);
        await worker.PullOnceAsync(CancellationToken.None);
    }

    public static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "ExamTransfer.slnx"))
               && !File.Exists(Path.Combine(directory.FullName, "ExamTransfer.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        if (File.Exists(Path.Combine(directory!.FullName, "ExamTransfer.sln"))
            && normalized.StartsWith($"backend{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            normalized = normalized[("backend".Length + 1)..];
        return File.ReadAllText(Path.Combine(directory.FullName, normalized));
    }

    private static AppDbContext CreateContext(string path) => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options);

    internal sealed class TestDatabase(string directory, string path, AppDbContext context) : IAsyncDisposable
    {
        public string Path { get; } = path;
        public AppDbContext Context { get; } = context;
        public AppDbContext CreateContext() => PublicCloudTestHarness.CreateContext(Path);
        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private sealed class NoOpRealtimePublisher : IRealtimePublisher
    {
        public Task PublishSessionAsync<T>(Guid sessionId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishParticipantAsync<T>(Guid sessionId, Guid participantId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

internal class RecordingCloudAdapter : ICloudAdapter
{
    public int ApproveCalls { get; private set; }
    public int ResubmitCalls { get; private set; }
    public int RejectSubmissionCalls { get; private set; }
    public Guid? LastApproveRequestId { get; private set; }
    public Guid? LastResubmitRequestId { get; private set; }
    public Guid? LastRejectSubmissionRequestId { get; private set; }
    public bool FailApprove { get; init; }
    public bool Enabled => true;
    public bool Configured => true;
    public bool Authenticated => true;
    public bool CanSynchronize => true;
    public CloudLoginResult? CurrentSession => null;
    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    public Task<CloudPreflightResult> PreflightAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<CloudPushResult> PushAsync(SyncQueueItem item, Func<CancellationToken, Task>? checkpoint, CancellationToken cancellationToken) => throw new NotSupportedException();
    public virtual Task<CloudPullPage> PullAsync(
        string entityName, CloudPullCursorValue cursor, int limit, CancellationToken cancellationToken) =>
        Task.FromResult(new CloudPullPage([], false));
    public Task<CloudParticipantMutationResult> ApprovePublicParticipantAsync(Guid sessionId, Guid participantId, Guid requestId, CancellationToken cancellationToken)
    {
        ApproveCalls++;
        LastApproveRequestId = requestId;
        if (FailApprove) throw new ApiException(ErrorCodes.CloudUploadFailed, "Simulated RPC failure", 502);
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(new CloudParticipantMutationResult(
            participantId, sessionId, ParticipantStatus.Approved, now, 0, false, null, 42, now));
    }
    public Task<CloudParticipantMutationResult> AllowPublicResubmissionAsync(
        Guid participantId, string reason, Guid requestId, CancellationToken cancellationToken)
    {
        ResubmitCalls++;
        LastResubmitRequestId = requestId;
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(new CloudParticipantMutationResult(
            participantId, Guid.Empty, ParticipantStatus.Approved, now, 0, true, reason, 43, now));
    }
    public Task<CloudSubmissionMutationResult> RejectPublicSubmissionAsync(
        Guid submissionId, string reason, Guid requestId, CancellationToken cancellationToken)
    {
        RejectSubmissionCalls++;
        LastRejectSubmissionRequestId = requestId;
        return Task.FromResult(new CloudSubmissionMutationResult(
            submissionId, Guid.Empty, Guid.Empty, SubmissionStatus.Rejected, reason, 44, DateTimeOffset.UtcNow));
    }
    public Task<CloudLoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<CloudLoginResult?> RefreshSessionAsync(CancellationToken cancellationToken) => Task.FromResult<CloudLoginResult?>(null);
    public Task LogoutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<IReadOnlyList<CloudBackupDescriptor>> ListBackupsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CloudBackupDescriptor>>([]);
    public Task DownloadObjectAsync(string cloudObjectPath, string destinationPath, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class PullCloudAdapter(IReadOnlyDictionary<string, CloudPullRecord> records) : RecordingCloudAdapter
{
    public override string ToString() => nameof(PullCloudAdapter);

    public override Task<CloudPullPage> PullAsync(
        string entityName, CloudPullCursorValue cursor, int limit, CancellationToken cancellationToken)
    {
        if (records.TryGetValue(entityName, out var record) && cursor.CloudVersion < record.CloudVersion)
            return Task.FromResult(new CloudPullPage([record], false));
        return Task.FromResult(new CloudPullPage([], false));
    }
}
