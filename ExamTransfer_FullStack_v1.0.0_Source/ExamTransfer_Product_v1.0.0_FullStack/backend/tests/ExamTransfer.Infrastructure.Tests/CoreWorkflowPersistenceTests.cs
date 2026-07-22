using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Security;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.Infrastructure.Storage;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class CoreWorkflowPersistenceTests
{
    [Fact]
    public async Task Dashboard_AfterClassCreation_ReturnsUpdatedRealClassCount()
    {
        await using var database = await FileDatabase.CreateAsync();

        int initialClassCount;
        await using (var initialContext = database.CreateContext())
        {
            initialClassCount = (await DashboardService(initialContext).GetDashboardAsync(CancellationToken.None)).ClassCount;
        }

        Guid classId;
        await using (var createContext = database.CreateContext())
        {
            var created = await Services(createContext).Classes.CreateAsync(
                new("Dashboard refresh", "DASH-REFRESH", "2026-2027", "Physical SQLite test"),
                CancellationToken.None);
            classId = created.Id;
        }

        await using (var refreshedContext = database.CreateContext())
        {
            var refreshed = await DashboardService(refreshedContext).GetDashboardAsync(CancellationToken.None);
            Assert.Equal(initialClassCount + 1, refreshed.ClassCount);
        }

        await using (var reopenedContext = database.CreateContext())
        {
            var reopened = await DashboardService(reopenedContext).GetDashboardAsync(CancellationToken.None);
            Assert.Equal(initialClassCount + 1, reopened.ClassCount);
        }

        await using (var archiveContext = database.CreateContext())
        {
            await Services(archiveContext).Classes.ArchiveAsync(classId, CancellationToken.None);
        }

        await using (var archivedContext = database.CreateContext())
        {
            var archived = await DashboardService(archivedContext).GetDashboardAsync(CancellationToken.None);
            Assert.Equal(initialClassCount, archived.ClassCount);
        }
    }

    [Fact]
    public async Task Class_Create_PersistsAcrossNewDbContext()
    {
        await using var database = await FileDatabase.CreateAsync();
        var created = await Services(database.Context).Classes.CreateAsync(
            new("Lớp 10A", "10A", "2026-2027", "Persistence"), CancellationToken.None);

        await using var restarted = database.CreateContext();
        var persisted = await restarted.ClassesSet.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.Equal("10A", persisted.Code);
    }

    [Fact]
    public async Task Class_Update_PersistsAcrossNewDbContext()
    {
        await using var database = await FileDatabase.CreateAsync();
        var classes = Services(database.Context).Classes;
        var created = await classes.CreateAsync(new("Lớp cũ", "OLD", "2026-2027", null), CancellationToken.None);
        var updated = await classes.UpdateAsync(created.Id, new("Lớp mới", "NEW", "2026-2027", "Updated", created.RowVersion), CancellationToken.None);

        await using var restarted = database.CreateContext();
        var persisted = await restarted.ClassesSet.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.Equal(updated.RowVersion, persisted.RowVersion);
        Assert.Equal("NEW", persisted.Code);
    }

    [Fact]
    public async Task Exam_CreateLinkedToClass_PersistsAcrossNewDbContext()
    {
        await using var database = await FileDatabase.CreateAsync();
        var services = Services(database.Context);
        var classroom = await services.Classes.CreateAsync(new("10B", "10B", "2026-2027", null), CancellationToken.None);
        var exam = await services.Exams.CreateAsync(ExamRequest(classroom.Id, false), CancellationToken.None);

        await using var restarted = database.CreateContext();
        Assert.Equal(classroom.Id, (await restarted.ExamsSet.AsNoTracking().SingleAsync(x => x.Id == exam.Id)).ClassId);
    }

    [Fact]
    public async Task Exam_PublishWithoutFile_Succeeds_WhenRuleDoesNotRequireFile()
    {
        await using var database = await FileDatabase.CreateAsync();
        var exams = Services(database.Context).Exams;
        var exam = await exams.CreateAsync(ExamRequest(null, false), CancellationToken.None);

        var published = await exams.PublishAsync(exam.Id, CancellationToken.None);

        Assert.Equal(ExamStatus.Published, published.Status);
    }

    [Fact]
    public async Task Exam_PublishWithoutFile_Fails_WhenRuleRequiresFile()
    {
        await using var database = await FileDatabase.CreateAsync();
        var exams = Services(database.Context).Exams;
        var exam = await exams.CreateAsync(ExamRequest(null, true), CancellationToken.None);

        var error = await Assert.ThrowsAsync<ApiException>(() => exams.PublishAsync(exam.Id, CancellationToken.None));

        Assert.Equal(ErrorCodes.ValidationFailed, error.Code);
        Assert.Equal(422, error.StatusCode);
        database.Context.ChangeTracker.Clear();
        Assert.Equal(ExamStatus.Draft, (await database.Context.ExamsSet.SingleAsync(x => x.Id == exam.Id)).Status);
    }

    [Fact]
    public async Task Exam_ListAfterPublish_WorksOnSqlite_AndIncludesCompletedFileCount()
    {
        await using var database = await FileDatabase.CreateAsync();
        var services = Services(database.Context);
        var exam = await services.Exams.CreateAsync(ExamRequest(null, false), CancellationToken.None);
        await services.Exams.PublishAsync(exam.Id, CancellationToken.None);

        var page = await services.Exams.ListAsync(null, ExamStatus.Published, 1, 50, CancellationToken.None);

        var listed = Assert.Single(page.Items);
        Assert.Equal(exam.Id, listed.Id);
        Assert.Equal(ExamStatus.Published, listed.Status);
    }

    [Fact]
    public async Task ClassAndSessionLists_WorkOnSqlite_WithDateTimeOffsetSortKeys()
    {
        await using var database = await FileDatabase.CreateAsync();
        var services = Services(database.Context);
        var classroom = await services.Classes.CreateAsync(new("List", "LIST", "2026-2027", null), CancellationToken.None);
        var exam = await services.Exams.CreateAsync(ExamRequest(classroom.Id, false), CancellationToken.None);
        await services.Exams.PublishAsync(exam.Id, CancellationToken.None);
        var session = await services.Sessions.CreateAsync(SessionRequest(exam.Id, classroom.Id, "LIST01"), "test-host", CancellationToken.None);

        var classes = await services.Classes.ListAsync(null, 1, 50, CancellationToken.None);
        var sessions = await services.Sessions.ListAsync(null, 1, 50, CancellationToken.None);

        Assert.Contains(classes.Items, x => x.Id == classroom.Id);
        Assert.Contains(sessions.Items, x => x.Id == session.Summary.Id);
    }

    [Fact]
    public async Task PublishedExam_CanCreateSession_WithSameClass()
    {
        await using var database = await FileDatabase.CreateAsync();
        var services = Services(database.Context);
        var classroom = await services.Classes.CreateAsync(new("10C", "10C", "2026-2027", null), CancellationToken.None);
        var exam = await services.Exams.CreateAsync(ExamRequest(classroom.Id, false), CancellationToken.None);
        await services.Exams.PublishAsync(exam.Id, CancellationToken.None);

        var session = await services.Sessions.CreateAsync(SessionRequest(exam.Id, classroom.Id, "ROOM10C"), "test-host", CancellationToken.None);

        Assert.Equal(classroom.Id, await database.Context.ExamSessionsSet.Where(x => x.Id == session.Summary.Id).Select(x => x.ClassId).SingleAsync());
    }

    [Fact]
    public async Task Session_ValidLifecycle_Works()
    {
        await using var database = await FileDatabase.CreateAsync();
        var services = Services(database.Context);
        var exam = await services.Exams.CreateAsync(ExamRequest(null, false), CancellationToken.None);
        await services.Exams.PublishAsync(exam.Id, CancellationToken.None);
        var session = await services.Sessions.CreateAsync(SessionRequest(exam.Id, null, "FLOW01"), "test-host", CancellationToken.None);

        foreach (var target in new[] { SessionStatus.Waiting, SessionStatus.Distributing, SessionStatus.InProgress, SessionStatus.Paused, SessionStatus.InProgress, SessionStatus.Collecting, SessionStatus.Finished })
            session = await services.Sessions.TransitionAsync(session.Summary.Id, target, target == SessionStatus.Finished ? new(false, null) : null, CancellationToken.None);

        Assert.Equal(SessionStatus.Finished, session.Summary.Status);
    }

    [Fact]
    public async Task Session_InvalidTransition_ReturnsInvalidStateTransition_AndLeavesStateUnchanged()
    {
        await using var database = await FileDatabase.CreateAsync();
        var services = Services(database.Context);
        var exam = await services.Exams.CreateAsync(ExamRequest(null, false), CancellationToken.None);
        await services.Exams.PublishAsync(exam.Id, CancellationToken.None);
        var session = await services.Sessions.CreateAsync(SessionRequest(exam.Id, null, "BADFLOW"), "test-host", CancellationToken.None);

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => services.Sessions.TransitionAsync(session.Summary.Id, SessionStatus.Paused, null, CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidStateTransition, error.Code);
        database.Context.ChangeTracker.Clear();
        Assert.Equal(SessionStatus.Draft, (await database.Context.ExamSessionsSet.SingleAsync(x => x.Id == session.Summary.Id)).Status);
    }

    [Fact]
    public async Task RealtimeFailure_AfterCommit_DoesNotTurnLocalTransitionIntoFailure()
    {
        await using var database = await FileDatabase.CreateAsync();
        var services = Services(database.Context, new ThrowingRealtimePublisher());
        var exam = await services.Exams.CreateAsync(ExamRequest(null, false), CancellationToken.None);
        await services.Exams.PublishAsync(exam.Id, CancellationToken.None);
        var session = await services.Sessions.CreateAsync(SessionRequest(exam.Id, null, "RTFAIL"), "test-host", CancellationToken.None);

        var transitioned = await services.Sessions.TransitionAsync(session.Summary.Id, SessionStatus.Waiting, null, CancellationToken.None);

        Assert.Equal(SessionStatus.Waiting, transitioned.Summary.Status);
        database.Context.ChangeTracker.Clear();
        Assert.Equal(SessionStatus.Waiting, (await database.Context.ExamSessionsSet.SingleAsync(x => x.Id == session.Summary.Id)).Status);
    }

    [Fact]
    public async Task CloudOffline_DoesNotBreakLocalCreatePublishSessionWorkflow()
    {
        await using var database = await FileDatabase.CreateAsync();
        var services = Services(database.Context);
        var classroom = await services.Classes.CreateAsync(new("Offline", "OFFLINE", "2026-2027", null), CancellationToken.None);
        var exam = await services.Exams.CreateAsync(ExamRequest(classroom.Id, false), CancellationToken.None);
        await services.Exams.PublishAsync(exam.Id, CancellationToken.None);
        var session = await services.Sessions.CreateAsync(SessionRequest(exam.Id, classroom.Id, "OFFLINE1"), "test-host", CancellationToken.None);

        Assert.Equal(SessionStatus.Draft, session.Summary.Status);
        Assert.True(await database.Context.SyncQueueSet.AnyAsync(x => x.EntityId == classroom.Id.ToString()));
        Assert.True(await database.Context.SyncQueueSet.AnyAsync(x => x.EntityId == exam.Id.ToString()));
        Assert.True(await database.Context.SyncQueueSet.AnyAsync(x => x.EntityId == session.Summary.Id.ToString()));
    }

    [Fact]
    public async Task RestartPersistence_RetainsIdsAndFinalStates_InSamePhysicalSqliteFile()
    {
        await using var database = await FileDatabase.CreateAsync();
        var services = Services(database.Context);
        var classroom = await services.Classes.CreateAsync(new("Restart", "RESTART", "2026-2027", null), CancellationToken.None);
        var exam = await services.Exams.CreateAsync(ExamRequest(classroom.Id, false), CancellationToken.None);
        await services.Exams.PublishAsync(exam.Id, CancellationToken.None);
        var session = await services.Sessions.CreateAsync(SessionRequest(exam.Id, classroom.Id, "RESTART1"), "test-host", CancellationToken.None);
        await services.Sessions.TransitionAsync(session.Summary.Id, SessionStatus.Waiting, null, CancellationToken.None);

        await database.Context.DisposeAsync();
        await using var restarted = database.CreateContext();
        Assert.True(await restarted.ClassesSet.AnyAsync(x => x.Id == classroom.Id));
        Assert.Equal(ExamStatus.Published, (await restarted.ExamsSet.SingleAsync(x => x.Id == exam.Id)).Status);
        Assert.Equal(SessionStatus.Waiting, (await restarted.ExamSessionsSet.SingleAsync(x => x.Id == session.Summary.Id)).Status);
    }

    private static CreateExamRequest ExamRequest(Guid? classId, bool requireFile) => new(
        classId,
        "Core workflow exam",
        "Integration",
        null,
        60,
        new FileRuleDto([".txt"], 1024 * 1024, 2 * 1024 * 1024, 2, false, requireFile));

    private static CreateSessionRequest SessionRequest(Guid examId, Guid? classId, string roomCode) =>
        new(examId, classId, DateTimeOffset.UtcNow.AddMinutes(5), "{}", false, 40, roomCode);

    private static ServiceSet Services(AppDbContext db, IRealtimePublisher? realtime = null)
    {
        realtime ??= new NoOpRealtimePublisher();
        var options = Options.Create(new ExamTransferOptions());
        var audit = new AuditService(db, new HttpContextAccessor());
        var outbox = new OutboxService(db);
        var paths = new TestStoragePaths(Path.Combine(Path.GetDirectoryName(db.Database.GetDbConnection().DataSource)!, "storage"));
        return new(
            new ClassService(db, new MemoryCache(new MemoryCacheOptions()), audit, outbox),
            new ExamService(db, paths, new ChunkStorage(), audit, outbox, realtime, options, NullLogger<ExamService>.Instance),
            new SessionService(db, new SessionTokenService(options), audit, outbox, realtime, options, NullLogger<SessionService>.Instance));
    }

    private static SystemService DashboardService(AppDbContext db)
    {
        var options = Options.Create(new ExamTransferOptions());
        var paths = new TestStoragePaths(Path.Combine(Path.GetDirectoryName(db.Database.GetDbConnection().DataSource)!, "storage"));
        return new SystemService(db, paths, new OfflineCloudAdapter(), options, new NoOpRealtimePublisher());
    }

    private sealed record ServiceSet(ClassService Classes, ExamService Exams, SessionService Sessions);

    private sealed class NoOpRealtimePublisher : IRealtimePublisher
    {
        public Task PublishSessionAsync<T>(Guid sessionId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishParticipantAsync<T>(Guid sessionId, Guid participantId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ThrowingRealtimePublisher : IRealtimePublisher
    {
        public Task PublishSessionAsync<T>(Guid sessionId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default) => throw new IOException("Simulated realtime outage");
        public Task PublishParticipantAsync<T>(Guid sessionId, Guid participantId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default) => throw new IOException("Simulated realtime outage");
    }

    private sealed class OfflineCloudAdapter : ICloudAdapter
    {
        public bool Enabled => false;
        public bool Configured => false;
        public bool Authenticated => false;
        public bool CanSynchronize => false;
        public CloudLoginResult? CurrentSession => null;

        public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<CloudPreflightResult> PreflightAsync(CancellationToken cancellationToken) => Task.FromResult(
            new CloudPreflightResult(false, false, false, false, "None", null, "Disabled", [], [], CloudAccessModes.UserSession, false, null, false));

        public Task<CloudPushResult> PushAsync(SyncQueueItem item, Func<CancellationToken, Task>? checkpoint, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CloudLoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CloudLoginResult?> RefreshSessionAsync(CancellationToken cancellationToken) => Task.FromResult<CloudLoginResult?>(null);
        public Task LogoutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<CloudBackupDescriptor>> ListBackupsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CloudBackupDescriptor>>([]);
        public Task DownloadObjectAsync(string cloudObjectPath, string destinationPath, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestStoragePaths(string root) : IStoragePaths
    {
        public string RootPath { get; } = root;
        public string DatabasePath => Path.Combine(RootPath, "database", "exam-transfer.db");
        public string BackupRoot => Path.Combine(RootPath, "backups");
        public string ExportRoot => Path.Combine(RootPath, "exports");
        public string TemporaryRoot => Path.Combine(RootPath, "temporary");
        public string ExamVersionRoot(Guid examId, int version) => Path.Combine(RootPath, "exams", examId.ToString("N"), $"v{version}");
        public string SessionRoot(Guid sessionId) => Path.Combine(RootPath, "sessions", sessionId.ToString("N"));
        public string SubmissionRoot(Guid sessionId, string studentCode, Guid submissionId) => Path.Combine(SessionRoot(sessionId), studentCode, submissionId.ToString("N"));
        public string ReceiptRoot(Guid sessionId) => Path.Combine(SessionRoot(sessionId), "receipts");
        public void EnsureCreated() => Directory.CreateDirectory(RootPath);
    }

    private sealed class FileDatabase : IAsyncDisposable
    {
        private readonly string directory;
        private readonly string databasePath;

        private FileDatabase(string directory, string databasePath, AppDbContext context)
        {
            this.directory = directory;
            this.databasePath = databasePath;
            Context = context;
        }

        public AppDbContext Context { get; }

        public static async Task<FileDatabase> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "ExamTransfer.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "workflow.db");
            var context = CreateContext(path);
            await context.Database.EnsureCreatedAsync();
            return new(directory, path, context);
        }

        public AppDbContext CreateContext() => CreateContext(databasePath);

        private static AppDbContext CreateContext(string path) => new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}
