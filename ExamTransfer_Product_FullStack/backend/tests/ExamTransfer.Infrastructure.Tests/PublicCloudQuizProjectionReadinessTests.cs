using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Execution.PublicCloud;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class PublicCloudQuizProjectionReadinessTests
{
    [Fact]
    public async Task QuizGraph_AllLatestRowsSynced_IsReady()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var fixture = await SeedQuizAsync(database.Context);
        AddGraphRows(database.Context, fixture, SyncStatus.Synced);
        await database.Context.SaveChangesAsync();

        var readiness = await Execution(database.Context)
            .GetProjectionReadinessAsync(fixture.Session.Id, default);

        Assert.True(readiness.Ready);
        Assert.Equal("PUBLICCLOUD_QUIZ_PROJECTION_READY", readiness.Code);
        Assert.Equal(SyncStatus.Synced, readiness.Status);
    }

    [Fact]
    public async Task QuizGraph_PendingQuestion_IsPending()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var fixture = await SeedQuizAsync(database.Context);
        AddGraphRows(database.Context, fixture, SyncStatus.Synced);
        AddRow(database.Context, "quiz_questions", fixture.Questions[0].Id, SyncStatus.Pending, 1);
        await database.Context.SaveChangesAsync();

        var readiness = await Execution(database.Context)
            .GetProjectionReadinessAsync(fixture.Session.Id, default);

        Assert.False(readiness.Ready);
        Assert.Equal("PUBLICCLOUD_QUIZ_PROJECTION_PENDING", readiness.Code);
        Assert.Equal(SyncStatus.Pending, readiness.Status);
    }

    [Fact]
    public async Task QuizGraph_FailedChoice_UsesNewestRowAndFails()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var fixture = await SeedQuizAsync(database.Context);
        AddGraphRows(database.Context, fixture, SyncStatus.Synced);
        AddRow(database.Context, "quiz_choices", fixture.Choices[0].Id, SyncStatus.Failed, 1);
        await database.Context.SaveChangesAsync();

        var readiness = await Execution(database.Context)
            .GetProjectionReadinessAsync(fixture.Session.Id, default);

        Assert.False(readiness.Ready);
        Assert.Equal("PUBLICCLOUD_QUIZ_PROJECTION_FAILED", readiness.Code);
        Assert.Equal(SyncStatus.Failed, readiness.Status);
    }

    [Theory]
    [InlineData("quiz_questions")]
    [InlineData("quiz_choices")]
    public async Task QuizGraph_MissingEntityQueueRow_IsNotReady(string missingEntityType)
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var fixture = await SeedQuizAsync(database.Context);
        AddGraphRows(
            database.Context,
            fixture,
            SyncStatus.Synced,
            missingEntityType == "quiz_questions" ? fixture.Questions[0].Id : fixture.Choices[0].Id);
        await database.Context.SaveChangesAsync();

        var readiness = await Execution(database.Context)
            .GetProjectionReadinessAsync(fixture.Session.Id, default);

        Assert.False(readiness.Ready);
        Assert.Equal(ErrorCodes.PublicCloudQuizProjectionNotReady, readiness.Code);
    }

    [Fact]
    public async Task QuizGraph_OtherExamVersionPending_DoesNotAffectCurrentSession()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var fixture = await SeedQuizAsync(database.Context, version: 3);
        AddGraphRows(database.Context, fixture, SyncStatus.Synced);
        var otherVersionQuestion = new QuizQuestion
        {
            ExamId = fixture.Exam.Id,
            Version = 4,
            Order = 1,
            Text = "Other version",
            Points = 10,
            Multiple = false
        };
        database.Context.QuizQuestionsSet.Add(otherVersionQuestion);
        AddRow(database.Context, "quiz_questions", otherVersionQuestion.Id, SyncStatus.Pending, 1);
        await database.Context.SaveChangesAsync();

        var readiness = await Execution(database.Context)
            .GetProjectionReadinessAsync(fixture.Session.Id, default);

        Assert.True(readiness.Ready);
        Assert.Equal("PUBLICCLOUD_QUIZ_PROJECTION_READY", readiness.Code);
    }

    [Fact]
    public async Task FileSubmissionAndLanOnly_ReadinessContractsRemainUnchanged()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var file = await SeedQuizAsync(
            database.Context,
            deliveryType: ExamDeliveryType.FileSubmission,
            questionCount: 0);
        var lan = await SeedQuizAsync(
            database.Context,
            accessMode: SessionAccessMode.LanOnly,
            questionCount: 0);
        AddRow(database.Context, "exam_sessions", file.Session.Id, SyncStatus.Synced);
        await database.Context.SaveChangesAsync();

        var execution = Execution(database.Context);
        var fileReadiness = await execution.GetProjectionReadinessAsync(file.Session.Id, default);
        var lanReadiness = await execution.GetProjectionReadinessAsync(lan.Session.Id, default);

        Assert.True(fileReadiness.Ready);
        Assert.Equal("PUBLICCLOUD_PROJECTION_READY", fileReadiness.Code);
        Assert.True(lanReadiness.Ready);
        Assert.Equal("LAN_ONLY", lanReadiness.Code);
    }

    [Fact]
    public async Task QuizGraph_WithNoQuestions_IsNotReady()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var fixture = await SeedQuizAsync(database.Context, questionCount: 0);

        var readiness = await Execution(database.Context)
            .GetProjectionReadinessAsync(fixture.Session.Id, default);

        Assert.False(readiness.Ready);
        Assert.Equal(ErrorCodes.QuizHasNoQuestions, readiness.Code);
    }

    [Fact]
    public async Task QuizGraph_500Questions_DoesNotExceedSqliteParameterLimit()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var fixture = await SeedQuizAsync(database.Context, questionCount: 500);
        AddGraphRows(database.Context, fixture, SyncStatus.Synced);
        await database.Context.SaveChangesAsync();

        var readiness = await Execution(database.Context)
            .GetProjectionReadinessAsync(fixture.Session.Id, default);

        Assert.True(readiness.Ready);
        Assert.Equal("PUBLICCLOUD_QUIZ_PROJECTION_READY", readiness.Code);
    }

    [Fact]
    public async Task RetryProjection_RetriesFailedQuizRowsButKeepsSyncedSession()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var fixture = await SeedQuizAsync(database.Context);
        AddGraphRows(database.Context, fixture, SyncStatus.Synced);
        var question = AddRow(
            database.Context,
            "quiz_questions",
            fixture.Questions[0].Id,
            SyncStatus.Failed,
            1);
        var choice = AddRow(
            database.Context,
            "quiz_choices",
            fixture.Choices[0].Id,
            SyncStatus.Failed,
            1);
        foreach (var item in new[] { question, choice })
        {
            item.LastError = "failed";
            item.LeaseUntilUtc = DateTimeOffset.UtcNow.AddMinutes(5);
            item.NextRetryAtUtc = DateTimeOffset.UtcNow.AddMinutes(10);
            item.CompletedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            item.RetryCount = 4;
        }
        await database.Context.SaveChangesAsync();
        var signal = new RecordingSignal();
        var before = DateTimeOffset.UtcNow;

        var readiness = await Execution(database.Context, signal)
            .RetryProjectionAsync(fixture.Session.Id, default);

        Assert.False(readiness.Ready);
        Assert.Equal("PUBLICCLOUD_QUIZ_PROJECTION_PENDING", readiness.Code);
        Assert.Equal(1, signal.PulseCount);
        Assert.All(new[] { question, choice }, item =>
        {
            Assert.Equal(SyncStatus.Pending, item.Status);
            Assert.Equal(4, item.RetryCount);
            Assert.Null(item.LastError);
            Assert.Null(item.LeaseUntilUtc);
            Assert.Null(item.CompletedAtUtc);
            Assert.True(item.NextRetryAtUtc >= before);
        });
        Assert.Equal(
            SyncStatus.Synced,
            Latest(database.Context, "exam_sessions", fixture.Session.Id).Status);
    }

    [Fact]
    public async Task RetryProjection_RetriesFailedSessionAndGraphWithOneSignal()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var fixture = await SeedQuizAsync(database.Context);
        AddGraphRows(database.Context, fixture, SyncStatus.Failed);
        await database.Context.SaveChangesAsync();
        var signal = new RecordingSignal();

        await Execution(database.Context, signal)
            .RetryProjectionAsync(fixture.Session.Id, default);

        Assert.All(database.Context.SyncQueueSet.Local, item =>
            Assert.Equal(SyncStatus.Pending, item.Status));
        Assert.Equal(1, signal.PulseCount);
    }

    [Fact]
    public async Task RetryProjection_RoomCodeConflictUsesRecoveryFlowAndDoesNotMaskGraphFailure()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var fixture = await SeedQuizAsync(database.Context);
        AddGraphRows(database.Context, fixture, SyncStatus.Synced);
        var failedQuestion = AddRow(
            database.Context,
            "quiz_questions",
            fixture.Questions[0].Id,
            SyncStatus.Failed,
            1);
        var sessionConflict = AddRow(
            database.Context,
            "exam_sessions",
            fixture.Session.Id,
            SyncStatus.Conflict,
            1);
        sessionConflict.LastError = JsonSerializer.Serialize(
            new CloudSyncFailure(ErrorCodes.RoomCodeConflict, "conflict"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await database.Context.SaveChangesAsync();
        var signal = new RecordingSignal();

        var readiness = await Execution(database.Context, signal)
            .RetryProjectionAsync(fixture.Session.Id, default);

        Assert.Equal(ErrorCodes.RoomCodeConflict, readiness.Code);
        Assert.Equal(SyncStatus.Failed, failedQuestion.Status);
        Assert.Equal(0, signal.PulseCount);
    }

    [Fact]
    public async Task SessionStart_PublicCloudQuizNotReady_IsRejectedWithoutAuditOrOutboxMutation()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var fixture = await SeedQuizAsync(database.Context);
        AddRow(database.Context, "exam_sessions", fixture.Session.Id, SyncStatus.Synced);
        await database.Context.SaveChangesAsync();
        var auditCount = await database.Context.AuditLogsSet.CountAsync();
        var queueCount = await database.Context.SyncQueueSet.CountAsync();
        var service = PublicCloudTestHarness.CreateSessionService(
            database.Context,
            new RecordingCloudAdapter());

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            service.TransitionAsync(
                fixture.Session.Id,
                SessionStatus.InProgress,
                null,
                default));

        database.Context.ChangeTracker.Clear();
        Assert.Equal(ErrorCodes.PublicCloudQuizProjectionNotReady, error.Code);
        Assert.Equal(409, error.StatusCode);
        Assert.Equal(
            SessionStatus.Waiting,
            (await database.Context.ExamSessionsSet.FindAsync(fixture.Session.Id))!.Status);
        Assert.Equal(auditCount, await database.Context.AuditLogsSet.CountAsync());
        Assert.Equal(queueCount, await database.Context.SyncQueueSet.CountAsync());
    }

    [Fact]
    public async Task SessionStart_PublicCloudQuizReady_TransitionsNormally()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var fixture = await SeedQuizAsync(database.Context);
        AddGraphRows(database.Context, fixture, SyncStatus.Synced);
        await database.Context.SaveChangesAsync();
        var service = PublicCloudTestHarness.CreateSessionService(
            database.Context,
            new RecordingCloudAdapter());

        var detail = await service.TransitionAsync(
            fixture.Session.Id,
            SessionStatus.InProgress,
            null,
            default);

        Assert.Equal(SessionStatus.InProgress, detail.Summary.Status);
    }

    [Theory]
    [InlineData(SessionAccessMode.LanOnly, ExamDeliveryType.MultipleChoice)]
    [InlineData(SessionAccessMode.PublicCloud, ExamDeliveryType.FileSubmission)]
    public async Task SessionStart_NonPublicQuizFlowsRemainUnchanged(
        SessionAccessMode accessMode,
        ExamDeliveryType deliveryType)
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var fixture = await SeedQuizAsync(
            database.Context,
            accessMode,
            deliveryType,
            questionCount: 0);
        var service = PublicCloudTestHarness.CreateSessionService(
            database.Context,
            new RecordingCloudAdapter());

        var detail = await service.TransitionAsync(
            fixture.Session.Id,
            SessionStatus.InProgress,
            null,
            default);

        Assert.Equal(SessionStatus.InProgress, detail.Summary.Status);
    }

    [Fact]
    public async Task SessionResume_PublicCloudQuizDoesNotReapplyStartGate()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var fixture = await SeedQuizAsync(database.Context);
        fixture.Session.Status = SessionStatus.Paused;
        await database.Context.SaveChangesAsync();
        var service = PublicCloudTestHarness.CreateSessionService(
            database.Context,
            new RecordingCloudAdapter());

        var detail = await service.TransitionAsync(
            fixture.Session.Id,
            SessionStatus.InProgress,
            null,
            default);

        Assert.Equal(SessionStatus.InProgress, detail.Summary.Status);
    }

    private static PublicCloudProjectionExecution Execution(
        AppDbContext db,
        ICloudSyncSignal? signal = null) =>
        new(db, signal);

    private static async Task<QuizFixture> SeedQuizAsync(
        AppDbContext db,
        SessionAccessMode accessMode = SessionAccessMode.PublicCloud,
        ExamDeliveryType deliveryType = ExamDeliveryType.MultipleChoice,
        int questionCount = 2,
        int version = 2)
    {
        var exam = new Exam
        {
            Title = "PublicCloud quiz readiness",
            Subject = "Test",
            DurationMinutes = 45,
            Status = ExamStatus.Published,
            DeliveryType = deliveryType,
            Version = version
        };
        var session = new ExamSession
        {
            Exam = exam,
            ExamId = exam.Id,
            RoomCode = Guid.NewGuid().ToString("N")[..8],
            Status = SessionStatus.Waiting,
            HostDeviceId = "host",
            AccessMode = accessMode,
            AdmissionMode = SessionAdmissionMode.OpenRequest,
            DeliveryTypeSnapshot = deliveryType,
            ExamVersionSnapshot = version,
            AcceptingParticipants = true
        };
        var questions = Enumerable.Range(1, questionCount)
            .Select(index => new QuizQuestion
            {
                Exam = exam,
                ExamId = exam.Id,
                Version = version,
                Order = index,
                Text = $"Question {index}",
                Points = 10m / Math.Max(1, questionCount),
                Multiple = false
            })
            .ToList();
        var choices = questions
            .SelectMany(question => Enumerable.Range(1, 2)
                .Select(index => new QuizChoice
                {
                    Question = question,
                    QuestionId = question.Id,
                    Order = index,
                    Text = $"Choice {index}",
                    IsCorrect = index == 1
                }))
            .ToList();
        db.ExamSessionsSet.Add(session);
        db.QuizQuestionsSet.AddRange(questions);
        db.QuizChoicesSet.AddRange(choices);
        await db.SaveChangesAsync();
        return new(exam, session, questions, choices);
    }

    private static void AddGraphRows(
        AppDbContext db,
        QuizFixture fixture,
        SyncStatus status,
        Guid? missingId = null)
    {
        foreach (var entity in GraphEntities(fixture))
        {
            if (entity.Id != missingId)
                AddRow(db, entity.EntityType, entity.Id, status);
        }
    }

    private static IEnumerable<(string EntityType, Guid Id)> GraphEntities(
        QuizFixture fixture)
    {
        yield return ("exams", fixture.Exam.Id);
        foreach (var question in fixture.Questions)
            yield return ("quiz_questions", question.Id);
        foreach (var choice in fixture.Choices)
            yield return ("quiz_choices", choice.Id);
        yield return ("exam_sessions", fixture.Session.Id);
    }

    private static SyncQueueItem AddRow(
        AppDbContext db,
        string entityType,
        Guid entityId,
        SyncStatus status,
        int minuteOffset = 0)
    {
        var item = new SyncQueueItem
        {
            EntityType = entityType,
            EntityId = entityId.ToString(),
            Operation = "upsert",
            PayloadJson = "{}",
            Status = status,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(minuteOffset)
        };
        db.SyncQueueSet.Add(item);
        return item;
    }

    private static SyncQueueItem Latest(
        AppDbContext db,
        string entityType,
        Guid entityId) =>
        db.SyncQueueSet.Local
            .Where(x => x.EntityType == entityType
                && x.EntityId == entityId.ToString())
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Id)
            .First();

    private sealed record QuizFixture(
        Exam Exam,
        ExamSession Session,
        IReadOnlyList<QuizQuestion> Questions,
        IReadOnlyList<QuizChoice> Choices);

    private sealed class RecordingSignal : ICloudSyncSignal
    {
        public int PulseCount { get; private set; }
        public void Pulse() => PulseCount++;
        public Task<bool> WaitAsync(
            TimeSpan maximumDelay,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
