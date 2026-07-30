using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.LocalServer.Workers;
using ExamTransfer.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class WorkerSqliteRegressionTests
{
    [Fact]
    public async Task ExportWorker_ClaimsOldestQueuedJob_AndPersistsRunningStatus()
    {
        await using var database = await TestDatabase.CreateAsync();
        var baseline = new DateTimeOffset(2026, 7, 18, 8, 0, 0, TimeSpan.Zero);
        var oldestQueued = ExportJobAt(baseline.AddMinutes(-10), ExportStatus.Queued);
        oldestQueued.Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var newerQueued = Enumerable.Range(1, ExportWorker.CandidateLimit)
            .Select(index =>
            {
                var job = ExportJobAt(baseline.AddMinutes(index), ExportStatus.Queued);
                job.Id = Guid.Parse($"00000000-0000-0000-0000-{index:D12}");
                return job;
            })
            .ToList();
        var nonQueued = ExportJobAt(baseline.AddMinutes(-20), ExportStatus.Completed);
        database.Context.ExportJobsSet.AddRange(newerQueued);
        database.Context.ExportJobsSet.AddRange(nonQueued, oldestQueued);
        await database.Context.SaveChangesAsync();

        var claimed = await ExportWorker.ClaimNextQueuedJobAsync(database.Context, CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Equal(oldestQueued.Id, claimed.Id);
        Assert.Equal(ExportStatus.Running, claimed.Status);
        Assert.Equal(1, claimed.Progress);

        database.Context.ChangeTracker.Clear();
        var persisted = await database.Context.ExportJobsSet.SingleAsync(x => x.Id == oldestQueued.Id);
        Assert.Equal(ExportStatus.Running, persisted.Status);
        Assert.All(
            await database.Context.ExportJobsSet.Where(x => newerQueued.Select(job => job.Id).Contains(x.Id)).ToListAsync(),
            job => Assert.Equal(ExportStatus.Queued, job.Status));
        Assert.Equal(ExportStatus.Completed, (await database.Context.ExportJobsSet.SingleAsync(x => x.Id == nonQueued.Id)).Status);
    }

    [Fact]
    public async Task ExportWorker_ClaimHonorsCancellation()
    {
        await using var database = await TestDatabase.CreateAsync();
        database.Context.ExportJobsSet.Add(ExportJobAt(DateTimeOffset.UtcNow, ExportStatus.Queued));
        await database.Context.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ExportWorker.ClaimNextQueuedJobAsync(database.Context, cancellation.Token));
    }

    [Fact]
    public async Task HeartbeatWorker_DisconnectsOnlyStaleParticipantsInOpenSessions()
    {
        await using var database = await TestDatabase.CreateAsync();
        var cutoff = new DateTimeOffset(2026, 7, 18, 8, 0, 0, TimeSpan.Zero);
        var openSession = AddSession(database.Context, SessionStatus.InProgress, "OPEN01");
        var finishedSession = AddSession(database.Context, SessionStatus.Finished, "DONE01");
        var stale = AddParticipant(database.Context, openSession, "S001", ParticipantStatus.Approved, cutoff.AddSeconds(-1));
        var fresh = AddParticipant(database.Context, openSession, "S002", ParticipantStatus.Approved, cutoff.AddSeconds(1));
        var rejected = AddParticipant(database.Context, openSession, "S003", ParticipantStatus.Rejected, cutoff.AddMinutes(-1));
        var disconnected = AddParticipant(database.Context, openSession, "S004", ParticipantStatus.Disconnected, cutoff.AddMinutes(-1));
        var ended = AddParticipant(database.Context, finishedSession, "S005", ParticipantStatus.Approved, cutoff.AddMinutes(-1));
        await database.Context.SaveChangesAsync();
        var realtime = new RecordingRealtimePublisher();

        var changed = await HeartbeatMonitorWorker.DisconnectStaleParticipantsAsync(
            database.Context,
            realtime,
            cutoff,
            CancellationToken.None);

        Assert.Equal(1, changed);
        Assert.Equal([stale.Id], realtime.ParticipantIds);
        database.Context.ChangeTracker.Clear();
        Assert.Equal(ParticipantStatus.Disconnected, (await FindParticipantAsync(database.Context, stale.Id)).Status);
        Assert.Equal(ParticipantStatus.Approved, (await FindParticipantAsync(database.Context, fresh.Id)).Status);
        Assert.Equal(ParticipantStatus.Rejected, (await FindParticipantAsync(database.Context, rejected.Id)).Status);
        Assert.Equal(ParticipantStatus.Disconnected, (await FindParticipantAsync(database.Context, disconnected.Id)).Status);
        Assert.Equal(ParticipantStatus.Approved, (await FindParticipantAsync(database.Context, ended.Id)).Status);
        Assert.Equal(1, (await database.Context.ExamSessionsSet.SingleAsync(x => x.Id == openSession.Id)).Sequence);
    }

    [Fact]
    public async Task HeartbeatWorker_UsesStrictlyLessThanCutoff()
    {
        await using var database = await TestDatabase.CreateAsync();
        var cutoff = new DateTimeOffset(2026, 7, 18, 8, 0, 0, TimeSpan.Zero);
        var session = AddSession(database.Context, SessionStatus.Waiting, "BOUND1");
        var before = AddParticipant(database.Context, session, "B001", ParticipantStatus.Connected, cutoff.AddTicks(-1));
        var equal = AddParticipant(database.Context, session, "B002", ParticipantStatus.Connected, cutoff);
        var after = AddParticipant(database.Context, session, "B003", ParticipantStatus.Connected, cutoff.AddTicks(1));
        await database.Context.SaveChangesAsync();

        var changed = await HeartbeatMonitorWorker.DisconnectStaleParticipantsAsync(
            database.Context,
            new RecordingRealtimePublisher(),
            cutoff,
            CancellationToken.None);

        Assert.Equal(1, changed);
        database.Context.ChangeTracker.Clear();
        Assert.Equal(ParticipantStatus.Disconnected, (await FindParticipantAsync(database.Context, before.Id)).Status);
        Assert.Equal(ParticipantStatus.Connected, (await FindParticipantAsync(database.Context, equal.Id)).Status);
        Assert.Equal(ParticipantStatus.Connected, (await FindParticipantAsync(database.Context, after.Id)).Status);
    }

    [Fact]
    public async Task Sqlite_PersistsDateTimeOffsetColumnsAsText()
    {
        await using var database = await TestDatabase.CreateAsync();

        Assert.Equal("TEXT", await ColumnTypeAsync(database.Connection, "export_jobs", "CreatedAtUtc"));
        Assert.Equal("TEXT", await ColumnTypeAsync(database.Connection, "session_participants", "LastSeenUtc"));
    }

    [Fact]
    public async Task ExportOutbox_CoalescesNewestPendingSnapshot_OnSqlite()
    {
        await using var database = await TestDatabase.CreateAsync();
        var baseline = new DateTimeOffset(2026, 7, 18, 8, 0, 0, TimeSpan.Zero);
        var older = SyncItemAt(baseline.AddMinutes(-2), SyncStatus.Pending);
        var newest = SyncItemAt(baseline.AddMinutes(-1), SyncStatus.Failed);
        var completed = SyncItemAt(baseline, SyncStatus.Synced);
        database.Context.SyncQueueSet.AddRange(older, newest, completed);
        await database.Context.SaveChangesAsync();

        var outbox = new OutboxService(database.Context);
        await outbox.EnqueueAsync(
            "export_jobs",
            "export-1",
            "upsert",
            new { status = "completed" },
            cancellationToken: CancellationToken.None);

        database.Context.ChangeTracker.Clear();
        var rows = await database.Context.SyncQueueSet.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.Equal(SyncStatus.Pending, rows.Single(x => x.Id == newest.Id).Status);
        Assert.Contains("completed", rows.Single(x => x.Id == newest.Id).PayloadJson);
        Assert.Equal("{}", rows.Single(x => x.Id == older.Id).PayloadJson);
        Assert.Equal(SyncStatus.Synced, rows.Single(x => x.Id == completed.Id).Status);
    }

    private static ExportJob ExportJobAt(DateTimeOffset createdAtUtc, ExportStatus status) => new()
    {
        SessionId = Guid.NewGuid(),
        CreatedAtUtc = createdAtUtc,
        Status = status
    };

    private static SyncQueueItem SyncItemAt(DateTimeOffset createdAtUtc, SyncStatus status) => new()
    {
        EntityType = "export_jobs",
        EntityId = "export-1",
        Operation = "upsert",
        PayloadJson = "{}",
        Status = status,
        CreatedAtUtc = createdAtUtc
    };

    private static ExamSession AddSession(AppDbContext db, SessionStatus status, string roomCode)
    {
        var exam = new Exam { Title = roomCode, Subject = "Test", DurationMinutes = 60, Status = ExamStatus.Published };
        var session = new ExamSession { Exam = exam, RoomCode = roomCode, Status = status, HostDeviceId = "test-host" };
        db.ExamSessionsSet.Add(session);
        return session;
    }

    private static SessionParticipant AddParticipant(
        AppDbContext db,
        ExamSession session,
        string studentCode,
        ParticipantStatus status,
        DateTimeOffset? lastSeenUtc)
    {
        var participant = new SessionParticipant
        {
            Session = session,
            StudentCode = studentCode,
            DisplayName = studentCode,
            DeviceId = "device-" + studentCode,
            MachineName = "test-machine",
            AppVersion = "test",
            Status = status,
            LastSeenUtc = lastSeenUtc
        };
        db.SessionParticipantsSet.Add(participant);
        return participant;
    }

    private static Task<SessionParticipant> FindParticipantAsync(AppDbContext db, Guid id) =>
        db.SessionParticipantsSet.SingleAsync(x => x.Id == id);

    private static async Task<string?> ColumnTypeAsync(SqliteConnection connection, string table, string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                return reader.GetString(2);
        }
        return null;
    }

    private sealed class RecordingRealtimePublisher : IRealtimePublisher
    {
        public List<Guid> ParticipantIds { get; } = [];

        public Task PublishSessionAsync<T>(Guid sessionId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default)
        {
            if (payload is ParticipantConnectionChangedEvent changed)
                ParticipantIds.Add(changed.ParticipantId);
            return Task.CompletedTask;
        }

        public Task PublishParticipantAsync<T>(Guid sessionId, Guid participantId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private TestDatabase(SqliteConnection connection, AppDbContext context)
        {
            this.connection = connection;
            Connection = connection;
            Context = context;
        }

        public SqliteConnection Connection { get; }
        public AppDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new AppDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
