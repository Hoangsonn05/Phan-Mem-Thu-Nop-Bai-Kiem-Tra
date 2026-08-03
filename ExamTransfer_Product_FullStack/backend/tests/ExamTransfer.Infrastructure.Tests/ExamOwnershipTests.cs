using System.Security.Claims;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.Infrastructure.Storage;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class ExamOwnershipTests
{
    [Fact]
    public async Task CreateExam_AssignsAuthenticatedActorAsCreatedBy()
    {
        await using var fixture = await ExamOwnershipFixture.CreateAsync();

        var created = await fixture.Service.CreateAsync(
            ExamRequest("Owned exam"),
            CancellationToken.None);

        var entity = await fixture.Db.ExamsSet.AsNoTracking()
            .SingleAsync(exam => exam.Id == created.Id);
        Assert.Equal(fixture.ActorId, entity.CreatedBy);
    }

    [Fact]
    public async Task CloneExam_AssignsCloningActorAndDoesNotKeepSourceOwner()
    {
        await using var fixture = await ExamOwnershipFixture.CreateAsync();
        var sourceOwnerId = Guid.NewGuid();
        var source = new Exam
        {
            Title = "Source exam",
            Subject = "Security",
            DurationMinutes = 30,
            Status = ExamStatus.Published,
            CreatedBy = sourceOwnerId
        };
        fixture.Db.ExamsSet.Add(source);
        await fixture.Db.SaveChangesAsync();

        var clone = await fixture.Service.CloneAsync(source.Id, CancellationToken.None);

        var clonedEntity = await fixture.Db.ExamsSet.AsNoTracking()
            .SingleAsync(exam => exam.Id == clone.Id);
        Assert.Equal(fixture.ActorId, clonedEntity.CreatedBy);
        Assert.NotEqual(sourceOwnerId, clonedEntity.CreatedBy);
        Assert.Equal(sourceOwnerId, source.CreatedBy);
    }

    private static CreateExamRequest ExamRequest(string title) => new(
        null,
        title,
        "Security",
        null,
        30,
        new FileRuleDto([".zip"], 1024, 1024, 1, false, true));

    private sealed class ExamOwnershipFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly string root;

        private ExamOwnershipFixture(
            SqliteConnection connection,
            string root,
            AppDbContext db,
            ExamService service,
            Guid actorId)
        {
            this.connection = connection;
            this.root = root;
            Db = db;
            Service = service;
            ActorId = actorId;
        }

        public AppDbContext Db { get; }
        public ExamService Service { get; }
        public Guid ActorId { get; }

        public static async Task<ExamOwnershipFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "ExamTransfer.Tests",
                "ExamOwnership",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();

            var actorId = Guid.NewGuid();
            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, actorId.ToString()),
                    new Claim("sub", actorId.ToString()),
                    new Claim(ClaimTypes.Role, UserRole.Teacher.ToString())
                ],
                "test");
            var accessor = new FixedHttpContextAccessor
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(identity),
                    TraceIdentifier = "exam-ownership-test"
                }
            };
            var paths = new OwnershipStoragePaths(Path.Combine(root, "storage"));
            Directory.CreateDirectory(paths.RootPath);
            var service = new ExamService(
                db,
                paths,
                new ChunkStorage(),
                new AuditService(db, accessor),
                new OutboxService(db),
                new NoOpRealtimePublisher(),
                Options.Create(new ExamTransferOptions()),
                NullLogger<ExamService>.Instance,
                accessor);
            return new(connection, root, db, service, actorId);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private sealed class NoOpRealtimePublisher : IRealtimePublisher
    {
        public Task PublishSessionAsync<T>(
            Guid sessionId,
            string eventName,
            long sequence,
            T payload,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PublishParticipantAsync<T>(
            Guid sessionId,
            Guid participantId,
            string eventName,
            long sequence,
            T payload,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixedHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class OwnershipStoragePaths(string rootPath) : IStoragePaths
    {
        public string RootPath { get; } = rootPath;
        public string DatabasePath => Path.Combine(RootPath, "database", "exam-transfer.db");
        public string BackupRoot => Path.Combine(RootPath, "database", "backups");
        public string ExportRoot => Path.Combine(RootPath, "exports");
        public string TemporaryRoot => Path.Combine(RootPath, "temporary");
        public string ExamVersionRoot(Guid examId, int version) =>
            Path.Combine(RootPath, "exams", examId.ToString("N"), $"v{version}");
        public string SessionRoot(Guid sessionId) =>
            Path.Combine(RootPath, "sessions", sessionId.ToString("N"));
        public string SubmissionRoot(Guid sessionId, string studentCode, Guid submissionId) =>
            Path.Combine(SessionRoot(sessionId), "submissions", studentCode, submissionId.ToString("N"));
        public string ReceiptRoot(Guid sessionId) =>
            Path.Combine(SessionRoot(sessionId), "receipts");
        public void EnsureCreated() => Directory.CreateDirectory(RootPath);
    }
}
