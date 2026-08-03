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

public sealed class ExamAccessDistributionGateTests
{
    [Theory]
    [InlineData(SessionStatus.Distributing)]
    [InlineData(SessionStatus.InProgress)]
    [InlineData(SessionStatus.Paused)]
    [InlineData(SessionStatus.Collecting)]
    public async Task ApprovedStudent_AllowedState_CanReadManifestAndFile(SessionStatus status)
    {
        await using var fixture = await ExamAccessFixture.CreateAsync(status);

        var manifest = await fixture.Service.GetManifestAsync(
            fixture.Exam.Id,
            fixture.StudentAccess(),
            CancellationToken.None);
        var file = await fixture.Service.GetFileContentAsync(
            fixture.Exam.Id,
            fixture.File.Id,
            fixture.StudentAccess(),
            CancellationToken.None);

        Assert.Equal(fixture.Exam.Id, manifest.ExamId);
        Assert.Equal(fixture.ExpectedPath, file.Path);
    }

    [Fact]
    public async Task ApprovedStudent_Waiting_ManifestAndFileAreForbidden()
    {
        await using var fixture = await ExamAccessFixture.CreateAsync(SessionStatus.Waiting);

        await AssertForbiddenAsync(() => fixture.Service.GetManifestAsync(
            fixture.Exam.Id,
            fixture.StudentAccess(),
            CancellationToken.None));
        await AssertForbiddenAsync(() => fixture.Service.GetFileContentAsync(
            fixture.Exam.Id,
            fixture.File.Id,
            fixture.StudentAccess(),
            CancellationToken.None));
    }

    [Theory]
    [InlineData(SessionStatus.Draft)]
    [InlineData(SessionStatus.Finished)]
    [InlineData(SessionStatus.Cancelled)]
    [InlineData(SessionStatus.Archived)]
    public async Task ApprovedStudent_DeniedState_CannotReadManifest(SessionStatus status)
    {
        await using var fixture = await ExamAccessFixture.CreateAsync(status);

        await AssertForbiddenAsync(() => fixture.Service.GetManifestAsync(
            fixture.Exam.Id,
            fixture.StudentAccess(),
            CancellationToken.None));
    }

    [Theory]
    [InlineData(ParticipantStatus.PendingApproval)]
    [InlineData(ParticipantStatus.Rejected)]
    public async Task UnapprovedParticipant_CannotReadManifest(ParticipantStatus status)
    {
        await using var fixture = await ExamAccessFixture.CreateAsync(SessionStatus.Distributing);
        fixture.Participant.Status = status;
        await fixture.Db.SaveChangesAsync();

        await AssertForbiddenAsync(() => fixture.Service.GetManifestAsync(
            fixture.Exam.Id,
            fixture.StudentAccess(),
            CancellationToken.None));
    }

    [Fact]
    public async Task WrongSessionExamOrganizationOrMode_IsDenied()
    {
        await using var fixture = await ExamAccessFixture.CreateAsync(SessionStatus.Distributing);

        await AssertForbiddenAsync(() => fixture.Service.GetManifestAsync(
            fixture.Exam.Id,
            fixture.StudentAccess() with { SessionId = Guid.NewGuid() },
            CancellationToken.None));
        await AssertForbiddenAsync(() => fixture.Service.GetManifestAsync(
            Guid.NewGuid(),
            fixture.StudentAccess(),
            CancellationToken.None));
        await AssertForbiddenAsync(() => fixture.Service.GetManifestAsync(
            fixture.Exam.Id,
            fixture.StudentAccess() with { OrganizationId = "org-b" },
            CancellationToken.None));

        fixture.Session.AccessMode = SessionAccessMode.PublicCloud;
        await fixture.Db.SaveChangesAsync();
        await AssertForbiddenAsync(() => fixture.Service.GetManifestAsync(
            fixture.Exam.Id,
            fixture.StudentAccess(),
            CancellationToken.None));
    }

    [Theory]
    [InlineData(UserRole.Teacher)]
    [InlineData(UserRole.Admin)]
    public async Task ManagementRole_PreservesManifestAccess(UserRole role)
    {
        await using var fixture = await ExamAccessFixture.CreateAsync(SessionStatus.Waiting);

        var manifest = await fixture.Service.GetManifestAsync(
            fixture.Exam.Id,
            new(role, null, "org-a", null, null, SessionAccessMode.LanOnly),
            CancellationToken.None);

        Assert.Single(manifest.Files);
    }

    private static async Task AssertForbiddenAsync(Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal(403, error.StatusCode);
        Assert.Equal(ErrorCodes.Forbidden, error.Code);
    }

    private sealed class ExamAccessFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly string root;

        private ExamAccessFixture(
            SqliteConnection connection,
            string root,
            AppDbContext db,
            ExamService service,
            User student,
            Exam exam,
            ExamFile file,
            ExamSession session,
            SessionParticipant participant,
            string expectedPath)
        {
            this.connection = connection;
            this.root = root;
            Db = db;
            Service = service;
            Student = student;
            Exam = exam;
            File = file;
            Session = session;
            Participant = participant;
            ExpectedPath = expectedPath;
        }

        public AppDbContext Db { get; }
        public ExamService Service { get; }
        public User Student { get; }
        public Exam Exam { get; }
        public ExamFile File { get; }
        public ExamSession Session { get; }
        public SessionParticipant Participant { get; }
        public string ExpectedPath { get; }

        public ExamFileAccessContext StudentAccess() => new(
            UserRole.Student,
            Student.Id,
            Student.OrganizationId,
            Session.Id,
            Participant.Id,
            SessionAccessMode.LanOnly);

        public static async Task<ExamAccessFixture> CreateAsync(SessionStatus status)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();

            var root = Path.Combine(Path.GetTempPath(), "ExamTransfer.Tests", Guid.NewGuid().ToString("N"));
            var paths = new TestStoragePaths(root);
            paths.EnsureCreated();
            var student = new User
            {
                Username = $"student-{Guid.NewGuid():N}",
                DisplayName = "Student",
                Role = UserRole.Student,
                OrganizationId = "org-a"
            };
            var exam = new Exam
            {
                Title = "Distribution gate",
                Subject = "Security",
                DurationMinutes = 60,
                DeliveryType = ExamDeliveryType.FileSubmission,
                Status = ExamStatus.Published
            };
            var relativePath = Path.Combine("exams", exam.Id.ToString("N"), "v1", "exam.pdf");
            var expectedPath = Path.GetFullPath(Path.Combine(root, relativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            await System.IO.File.WriteAllTextAsync(expectedPath, "exam");
            var file = new ExamFile
            {
                Exam = exam,
                ExamId = exam.Id,
                Version = 1,
                OriginalName = "exam.pdf",
                RelativePath = relativePath,
                MimeType = "application/pdf",
                SizeBytes = new FileInfo(expectedPath).Length,
                Sha256 = new string('a', 64),
                TransferStatus = TransferStatus.Completed
            };
            var session = new ExamSession
            {
                Exam = exam,
                ExamId = exam.Id,
                RoomCode = $"GATE-{Guid.NewGuid():N}",
                Status = status,
                AccessMode = SessionAccessMode.LanOnly,
                DeliveryTypeSnapshot = ExamDeliveryType.FileSubmission
            };
            var participant = new SessionParticipant
            {
                Session = session,
                SessionId = session.Id,
                UserId = student.Id,
                StudentCode = "S001",
                DisplayName = "Student",
                DeviceId = "device",
                MachineName = "machine",
                AppVersion = "test",
                Status = ParticipantStatus.Approved
            };
            db.AddRange(student, exam, file, session, participant);
            await db.SaveChangesAsync();

            var service = new ExamService(
                db,
                paths,
                new ChunkStorage(),
                new AuditService(db, new HttpContextAccessor()),
                new OutboxService(db),
                new NoOpRealtimePublisher(),
                Options.Create(new ExamTransferOptions()),
                NullLogger<ExamService>.Instance,
                new HttpContextAccessor());
            return new(connection, root, db, service, student, exam, file, session, participant, expectedPath);
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
        public Task PublishSessionAsync<T>(Guid sessionId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishParticipantAsync<T>(Guid sessionId, Guid participantId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
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
}
