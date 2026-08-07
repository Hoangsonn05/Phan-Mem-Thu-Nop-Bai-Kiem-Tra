using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Execution.PublicCloud;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.LocalServer.Workers;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class PublicCloudTeacherProjectionTests
{
    [Theory]
    [InlineData(QuizResultPolicy.Hidden)]
    [InlineData(QuizResultPolicy.ShowAfterSubmission)]
    public async Task TeacherPublicCloudQuiz_FinalizedPulledAttempt_AppearsInSubmissionList(
        QuizResultPolicy resultPolicy)
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var seed = await SeedQuizSessionAsync(
            database.Context,
            SessionAccessMode.PublicCloud,
            resultPolicy);
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var finalizedAt = startedAt.AddMinutes(5);
        database.Context.QuizAttemptsSet.Add(new QuizAttempt
        {
            SessionId = seed.Session.Id,
            ParticipantId = seed.Participant.Id,
            SourceMode = "PublicCloud",
            CloudVersion = 42,
            CloudSyncState = "Pulled",
            AttemptNumber = 1,
            ExamVersion = seed.Session.ExamVersionSnapshot,
            Status = QuizAttemptStatus.Finalized,
            StartedAtUtc = startedAt,
            DeadlineUtc = finalizedAt.AddMinutes(10),
            FinalizedAtUtc = finalizedAt,
            AutoScore = 7.5m,
            Score = 7.5m,
            MaxScore = 10m,
            GradingStatus = GradingStatus.Graded,
            ResultPolicySnapshot = resultPolicy,
            SnapshotJson = "{}"
        });
        await database.Context.SaveChangesAsync();

        var service = CreateQuizService(database.Context);
        var row = Assert.Single(await service.ListTeacherSubmissionsForSessionAsync(
            seed.Session.Id,
            seed.Teacher.Id,
            seed.Teacher.OrganizationId,
            CancellationToken.None));

        Assert.Equal(seed.Participant.Id, row.ParticipantId);
        Assert.Equal(seed.Participant.StudentCode, row.StudentCode);
        Assert.Equal(seed.Participant.DisplayName, row.FullName);
        Assert.Equal(7.5m, row.Score);
        Assert.Equal(10m, row.MaxScore);
        Assert.Equal(startedAt, row.StartedAtUtc);
        Assert.Equal(finalizedAt, row.FinalizedAtUtc);
        Assert.Equal(300, row.DurationSeconds);
        Assert.Null(row.DataIssue);
    }

    [Fact]
    public async Task TeacherPublicCloudSession_DoesNotIncludeOnlyLanAttempt()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var seed = await SeedQuizSessionAsync(
            database.Context,
            SessionAccessMode.PublicCloud,
            QuizResultPolicy.Hidden);
        var localParticipant = Participant(seed.Session, "LAN-WRONG", "Lan Wrong", "Lan");
        database.Context.AddRange(
            FinalizedAttempt(seed.Session, seed.Participant, "PublicCloud", 8m),
            localParticipant,
            FinalizedAttempt(seed.Session, localParticipant, "Lan", 9m));
        await database.Context.SaveChangesAsync();

        var rows = await CreateQuizService(database.Context)
            .ListTeacherSubmissionsForSessionAsync(
                seed.Session.Id,
                seed.Teacher.Id,
                seed.Teacher.OrganizationId,
                CancellationToken.None);

        Assert.Equal(seed.Participant.Id, Assert.Single(rows).ParticipantId);
    }

    [Fact]
    public async Task TeacherOnlyLanSession_DoesNotIncludePublicCloudAttempt()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var seed = await SeedQuizSessionAsync(
            database.Context,
            SessionAccessMode.LanOnly,
            QuizResultPolicy.Hidden);
        var cloudParticipant = Participant(
            seed.Session,
            "PC-WRONG",
            "PublicCloud Wrong",
            "PublicCloud");
        database.Context.AddRange(
            FinalizedAttempt(seed.Session, seed.Participant, "Lan", 8m),
            cloudParticipant,
            FinalizedAttempt(seed.Session, cloudParticipant, "PublicCloud", 9m));
        await database.Context.SaveChangesAsync();

        var rows = await CreateQuizService(database.Context)
            .ListTeacherSubmissionsForSessionAsync(
                seed.Session.Id,
                seed.Teacher.Id,
                seed.Teacher.OrganizationId,
                CancellationToken.None);

        Assert.Equal(seed.Participant.Id, Assert.Single(rows).ParticipantId);
    }

    [Fact]
    public async Task PublicCloudMultipleChoice_FinalizedPulledAttempt_IncrementsSubmittedCountWithoutSourceMixing()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var seed = await SeedQuizSessionAsync(
            database.Context,
            SessionAccessMode.PublicCloud,
            QuizResultPolicy.Hidden);
        var localParticipant = Participant(seed.Session, "LAN-COUNT", "Lan Count", "Lan");
        database.Context.AddRange(
            FinalizedAttempt(seed.Session, seed.Participant, "PublicCloud", 7.5m),
            localParticipant,
            FinalizedAttempt(seed.Session, localParticipant, "Lan", 10m));
        await database.Context.SaveChangesAsync();

        var sessions = PublicCloudTestHarness.CreateSessionService(
            database.Context,
            new PullCloudAdapter(new Dictionary<string, CloudPullRecord>()));
        var detail = await sessions.GetAsync(seed.Session.Id, CancellationToken.None);

        Assert.Equal(1, detail.Summary.Counts.Submitted);
    }

    [Fact]
    public async Task OnlyLanMultipleChoice_DoesNotCountPublicCloudAttempt()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var seed = await SeedQuizSessionAsync(
            database.Context,
            SessionAccessMode.LanOnly,
            QuizResultPolicy.Hidden);
        var cloudParticipant = Participant(
            seed.Session,
            "PC-COUNT",
            "PublicCloud Count",
            "PublicCloud");
        database.Context.AddRange(
            FinalizedAttempt(seed.Session, seed.Participant, "Lan", 7.5m),
            cloudParticipant,
            FinalizedAttempt(seed.Session, cloudParticipant, "PublicCloud", 10m));
        await database.Context.SaveChangesAsync();

        var sessions = PublicCloudTestHarness.CreateSessionService(
            database.Context,
            new PullCloudAdapter(new Dictionary<string, CloudPullRecord>()));
        var detail = await sessions.GetAsync(seed.Session.Id, CancellationToken.None);

        Assert.Equal(1, detail.Summary.Counts.Submitted);
    }

    [Fact]
    public async Task TeacherPublicCloudQuiz_DeniesTeacherFromAnotherOrganization()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var seed = await SeedQuizSessionAsync(
            database.Context,
            SessionAccessMode.PublicCloud,
            QuizResultPolicy.Hidden);
        var stranger = new User
        {
            Username = $"stranger-{Guid.NewGuid():N}",
            DisplayName = "Other Teacher",
            Role = UserRole.Teacher,
            IsActive = true,
            OrganizationId = "org-other"
        };
        database.Context.UsersSet.Add(stranger);
        await database.Context.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            CreateQuizService(database.Context).ListTeacherSubmissionsForSessionAsync(
                seed.Session.Id,
                stranger.Id,
                stranger.OrganizationId,
                CancellationToken.None));

        Assert.Equal(403, error.StatusCode);
    }

    [Fact]
    public async Task PullQuizAttempt_PreservesAuthoritativeScoreTimingAndSourceMode()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var seed = await SeedQuizSessionAsync(
            database.Context,
            SessionAccessMode.PublicCloud,
            QuizResultPolicy.Hidden);
        var attemptId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-7);
        var finalizedAt = startedAt.AddMinutes(6);
        var record = QuizAttemptRecord(
            attemptId,
            seed.Session.Id,
            seed.Participant.Id,
            50,
            startedAt,
            finalizedAt,
            7.5m,
            QuizResultPolicy.Hidden);

        await PublicCloudTestHarness.RunPullOnceAsync(
            database.Path,
            new PullCloudAdapter(new Dictionary<string, CloudPullRecord>
            {
                ["quiz_attempts"] = record
            }));

        await using var verify = database.CreateContext();
        var attempt = await verify.QuizAttemptsSet.SingleAsync(x => x.Id == attemptId);
        Assert.Equal("PublicCloud", attempt.SourceMode);
        Assert.Equal(50, attempt.CloudVersion);
        Assert.Equal(QuizAttemptStatus.Finalized, attempt.Status);
        Assert.Equal(GradingStatus.Graded, attempt.GradingStatus);
        Assert.Equal(QuizResultPolicy.Hidden, attempt.ResultPolicySnapshot);
        Assert.Equal(startedAt, attempt.StartedAtUtc);
        Assert.Equal(finalizedAt, attempt.FinalizedAtUtc);
        Assert.Equal(7.5m, attempt.AutoScore);
        Assert.Equal(7.5m, attempt.Score);
        Assert.Equal(10m, attempt.MaxScore);
    }

    [Fact]
    public async Task PullQuizAttempt_NewerCloudVersion_PublishesTeacherRefreshAfterCommit()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var seed = await SeedQuizSessionAsync(
            database.Context,
            SessionAccessMode.PublicCloud,
            QuizResultPolicy.Hidden);
        var attemptId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var realtime = new RecordingRealtimePublisher();
        var cloud = new PullCloudAdapter(new Dictionary<string, CloudPullRecord>
        {
            ["quiz_attempts"] = QuizAttemptRecord(
                attemptId,
                seed.Session.Id,
                seed.Participant.Id,
                61,
                startedAt,
                startedAt.AddMinutes(5),
                7.5m,
                QuizResultPolicy.Hidden)
        });

        await RunPullOnceAsync(database.Path, cloud, realtime);

        var published = Assert.Single(realtime.Events);
        Assert.Equal(seed.Session.Id, published.SessionId);
        Assert.Equal(RealtimeEvents.PublicCloudProjectionUpdated, published.EventName);
        Assert.Equal(61, published.Sequence);
        Assert.Equal(PublicCloudProjectionEntityTypes.QuizAttempt, published.Payload.EntityType);
        await using var verify = database.CreateContext();
        Assert.Equal(61, (await verify.QuizAttemptsSet.SingleAsync(x => x.Id == attemptId)).CloudVersion);

        await RunPullOnceAsync(database.Path, cloud, realtime);
        Assert.Single(realtime.Events);

        var newerCloud = new PullCloudAdapter(new Dictionary<string, CloudPullRecord>
        {
            ["quiz_attempts"] = QuizAttemptRecord(
                attemptId,
                seed.Session.Id,
                seed.Participant.Id,
                62,
                startedAt,
                startedAt.AddMinutes(5),
                8m,
                QuizResultPolicy.Hidden)
        });
        await RunPullOnceAsync(database.Path, newerCloud, realtime);
        Assert.Equal(2, realtime.Events.Count);
        Assert.Equal(62, realtime.Events[1].Sequence);
        await using var verifyNewer = database.CreateContext();
        Assert.Equal(8m, (await verifyNewer.QuizAttemptsSet.SingleAsync(
            x => x.Id == attemptId)).Score);

        var staleCloud = new PullCloudAdapter(new Dictionary<string, CloudPullRecord>
        {
            ["quiz_attempts"] = QuizAttemptRecord(
                attemptId,
                seed.Session.Id,
                seed.Participant.Id,
                60,
                startedAt,
                startedAt.AddMinutes(5),
                9m,
                QuizResultPolicy.Hidden)
        });
        await RunPullOnceAsync(database.Path, staleCloud, realtime);
        Assert.Equal(2, realtime.Events.Count);
    }

    private static QuizService CreateQuizService(AppDbContext db) =>
        new(db, new QuizProjectionOutbox(new OutboxService(db)));

    private static async Task<Seed> SeedQuizSessionAsync(
        AppDbContext db,
        SessionAccessMode accessMode,
        QuizResultPolicy resultPolicy)
    {
        var teacher = new User
        {
            Username = $"teacher-{Guid.NewGuid():N}",
            DisplayName = "Teacher",
            Role = UserRole.Teacher,
            IsActive = true,
            OrganizationId = "org-pc4"
        };
        var exam = new Exam
        {
            Title = "PublicCloud quiz",
            Subject = "Test",
            DurationMinutes = 60,
            Status = ExamStatus.Published,
            DeliveryType = ExamDeliveryType.MultipleChoice,
            QuizResultPolicy = resultPolicy,
            Version = 3,
            CreatedBy = teacher.Id
        };
        var session = new ExamSession
        {
            Exam = exam,
            ExamId = exam.Id,
            RoomCode = Guid.NewGuid().ToString("N")[..8],
            HostDeviceId = "pc4-host",
            Status = SessionStatus.Collecting,
            AccessMode = accessMode,
            DeliveryTypeSnapshot = ExamDeliveryType.MultipleChoice,
            QuizResultPolicySnapshot = resultPolicy,
            ExamVersionSnapshot = exam.Version
        };
        var participant = Participant(
            session,
            "PC-001",
            "PublicCloud Student",
            accessMode == SessionAccessMode.PublicCloud ? "PublicCloud" : "Lan");
        db.AddRange(teacher, participant);
        await db.SaveChangesAsync();
        return new(teacher, session, participant);
    }

    private static SessionParticipant Participant(
        ExamSession session,
        string studentCode,
        string displayName,
        string sourceMode) =>
        new()
        {
            Session = session,
            SessionId = session.Id,
            StudentCode = studentCode,
            DisplayName = displayName,
            DeviceId = $"{studentCode}-device",
            MachineName = $"{studentCode}-machine",
            AppVersion = "pc4-test",
            Status = ParticipantStatus.Approved,
            SourceMode = sourceMode
        };

    private static QuizAttempt FinalizedAttempt(
        ExamSession session,
        SessionParticipant participant,
        string sourceMode,
        decimal score)
    {
        var finalizedAt = DateTimeOffset.UtcNow;
        return new()
        {
            Session = session,
            SessionId = session.Id,
            Participant = participant,
            ParticipantId = participant.Id,
            SourceMode = sourceMode,
            AttemptNumber = 1,
            ExamVersion = session.ExamVersionSnapshot,
            Status = QuizAttemptStatus.Finalized,
            StartedAtUtc = finalizedAt.AddMinutes(-5),
            DeadlineUtc = finalizedAt.AddMinutes(10),
            FinalizedAtUtc = finalizedAt,
            AutoScore = score,
            Score = score,
            MaxScore = 10m,
            GradingStatus = GradingStatus.Graded,
            ResultPolicySnapshot = session.QuizResultPolicySnapshot,
            SnapshotJson = "{}"
        };
    }

    private static CloudPullRecord QuizAttemptRecord(
        Guid attemptId,
        Guid sessionId,
        Guid participantId,
        long cloudVersion,
        DateTimeOffset startedAt,
        DateTimeOffset finalizedAt,
        decimal score,
        QuizResultPolicy resultPolicy) =>
        new(
            "quiz_attempts",
            attemptId.ToString(),
            cloudVersion,
            finalizedAt,
            JsonSerializer.Serialize(new
            {
                id = attemptId,
                session_id = sessionId,
                participant_id = participantId,
                attempt_number = 1,
                exam_version = 3,
                status = "Finalized",
                started_at = startedAt,
                deadline_at = finalizedAt.AddMinutes(10),
                finalized_at = finalizedAt,
                auto_score = score,
                score,
                max_score = 10m,
                grading_status = "Graded",
                result_policy = resultPolicy.ToString(),
                snapshot_json = new { questions = Array.Empty<object>() },
                finalize_idempotency_key = "pc4-finalize"
            }));

    private static async Task RunPullOnceAsync(
        string databasePath,
        ICloudAdapter cloud,
        IRealtimePublisher realtime)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton(cloud);
        await using var provider = services.BuildServiceProvider();
        var worker = new PublicCloudPullWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PublicCloudPullWorker>.Instance,
            realtime);
        await worker.PullOnceAsync(CancellationToken.None);
    }

    private sealed class RecordingRealtimePublisher : IRealtimePublisher
    {
        public List<PublishedEvent> Events { get; } = [];

        public Task PublishSessionAsync<T>(
            Guid sessionId,
            string eventName,
            long sequence,
            T payload,
            CancellationToken cancellationToken = default)
        {
            Events.Add(new(
                sessionId,
                eventName,
                sequence,
                Assert.IsType<PublicCloudProjectionUpdatedEvent>(payload)));
            return Task.CompletedTask;
        }

        public Task PublishParticipantAsync<T>(
            Guid sessionId,
            Guid participantId,
            string eventName,
            long sequence,
            T payload,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed record Seed(
        User Teacher,
        ExamSession Session,
        SessionParticipant Participant);

    private sealed record PublishedEvent(
        Guid SessionId,
        string EventName,
        long Sequence,
        PublicCloudProjectionUpdatedEvent Payload);
}
