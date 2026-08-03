using System.IO.Compression;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class UnifiedGradingTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SqliteUpgrade_BackfillsExistingFinalizedAttemptAndAdvancesSchema12()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "quiz_attempts"
            SET "AutoScore" = NULL,
                "GradingStatus" = 1,
                "GradedAtUtc" = NULL,
                "MaxScore" = '8.0'
            WHERE "Id" = {0}
            """,
            fixture.Attempt.Id);
        fixture.Db.ChangeTracker.Clear();

        await DbInitializer.InitializeAsync(fixture.Db, fixture.Paths);

        var upgraded = await fixture.Db.QuizAttemptsSet.AsNoTracking()
            .SingleAsync(x => x.Id == fixture.Attempt.Id);
        Assert.Equal(upgraded.Score, upgraded.AutoScore);
        Assert.Equal(10m, upgraded.MaxScore);
        Assert.Equal(GradingStatus.Graded, upgraded.GradingStatus);
        Assert.Equal(upgraded.FinalizedAtUtc, upgraded.GradedAtUtc);
        Assert.Equal(
            "\"12\"",
            (await fixture.Db.AppSettingsSet.SingleAsync(x => x.Key == "schema.version")).ValueJson);
    }

    [Fact]
    public async Task QuizGrade_AuthoritativeSaveReturnAndReopen_AreIdempotentAndEmitEvents()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.QuizGrades;
        var attempt = fixture.Attempt;

        var teacher = await service.GetAsync(attempt.Id, fixture.TeacherId, "org-a", default);
        Assert.Equal(8m, teacher.AutoScore);
        Assert.True(teacher.Questions[0].Choices.Single(x => x.Correct == true).Selected);

        var hidden = await service.GetStudentReviewAsync(attempt.Id, fixture.Participant.Id, default);
        Assert.False(hidden.ScoreVisible);
        Assert.False(hidden.CorrectAnswersVisible);
        Assert.Null(hidden.Score);
        Assert.All(hidden.Questions.SelectMany(x => x.Choices), x => Assert.Null(x.Correct));

        var saveRequestId = Guid.NewGuid();
        var keepAuto = await service.SaveAsync(
            attempt.Id,
            new(null, "Nhận xét", teacher.RowVersion, saveRequestId),
            fixture.TeacherId,
            "org-a",
            default);
        Assert.Equal(10m, keepAuto.AutoScore);
        Assert.Equal(10m, keepAuto.Score);
        var saveRetry = await service.SaveAsync(
            attempt.Id,
            new(null, "Nhận xét", teacher.RowVersion, saveRequestId),
            fixture.TeacherId,
            "org-a",
            default);
        Assert.Equal(keepAuto.RowVersion, saveRetry.RowVersion);
        Assert.Equal(keepAuto.Score, saveRetry.Score);
        await Assert.ThrowsAsync<ApiException>(() => service.SaveAsync(
            attempt.Id,
            new(7.5m, null, keepAuto.RowVersion),
            fixture.TeacherId,
            "org-a",
            default));
        await Assert.ThrowsAsync<ApiException>(() => service.SaveAsync(
            attempt.Id,
            new(10m, null, teacher.RowVersion),
            fixture.TeacherId,
            "org-a",
            default));

        var returnRequestId = Guid.NewGuid();
        var returned = await service.ReturnAsync(
            attempt.Id,
            new("Đã công bố", keepAuto.RowVersion, returnRequestId),
            fixture.TeacherId,
            "org-a",
            default);
        Assert.Equal(GradingStatus.Returned, returned.Status);
        Assert.Empty(fixture.Realtime.Events);
        Assert.Single(await fixture.Db.SyncQueueSet
            .Where(x => x.EntityType == OnlyLanStudentNotificationOutbox.EntityType)
            .ToListAsync());
        var visible = await service.GetStudentReviewAsync(attempt.Id, fixture.Participant.Id, default);
        Assert.True(visible.ScoreVisible);
        Assert.True(visible.CorrectAnswersVisible);
        Assert.Equal(10m, visible.Score);
        Assert.Contains(visible.Questions.SelectMany(x => x.Choices), x => x.Correct == true);
        await Assert.ThrowsAsync<ApiException>(() => service.SaveAsync(
            attempt.Id,
            new(6m, null, returned.RowVersion),
            fixture.TeacherId,
            "org-a",
            default));

        var returnRetry = await service.ReturnAsync(
            attempt.Id,
            new("Đã công bố", keepAuto.RowVersion, returnRequestId),
            fixture.TeacherId,
            "org-a",
            default);
        Assert.Equal(returned.RowVersion, returnRetry.RowVersion);
        Assert.Single(await fixture.Db.SyncQueueSet
            .Where(x => x.EntityType == OnlyLanStudentNotificationOutbox.EntityType)
            .ToListAsync());

        var reopenRequestId = Guid.NewGuid();
        var reopened = await service.ReopenAsync(
            attempt.Id,
            new("Rà soát lại", returned.RowVersion, reopenRequestId),
            fixture.TeacherId,
            "org-a",
            default);
        Assert.Equal(GradingStatus.Graded, reopened.Status);
        Assert.Equal(2, await fixture.Db.SyncQueueSet.CountAsync(
            x => x.EntityType == OnlyLanStudentNotificationOutbox.EntityType));
        var maskedAgain = await service.GetStudentReviewAsync(attempt.Id, fixture.Participant.Id, default);
        Assert.False(maskedAgain.ScoreVisible);
        Assert.False(maskedAgain.CorrectAnswersVisible);
        Assert.True(await fixture.Db.AuditLogsSet.AnyAsync(x => x.Action == "QuizGradeReturned"));
        Assert.True(await fixture.Db.SyncQueueSet.AnyAsync(x => x.EntityType == "quiz_attempts"));
        Assert.Equal(3, await fixture.Db.QuizGradeMutationReceiptsSet.CountAsync());
    }

    [Fact]
    public async Task GradingWorkItems_CombinesOfficialFilesAndFinalizedQuiz_AndEnforcesTenant()
    {
        await using var fixture = await Fixture.CreateAsync();

        var page = await fixture.QuizGrades.GetWorkItemsAsync(
            null,
            1,
            100,
            fixture.TeacherId,
            "org-a",
            default);

        Assert.Contains(page.Items, x => x.Type == GradingWorkItemType.FileSubmission && x.Id == fixture.Submission.Id);
        Assert.Contains(page.Items, x => x.Type == GradingWorkItemType.QuizAttempt && x.Id == fixture.Attempt.Id);
        var fileItem = page.Items.Single(x => x.Id == fixture.Submission.Id);
        Assert.Equal(fixture.Attempt.Session.ExamId, fileItem.ExamId);
        Assert.Equal(fixture.Submission.AttemptNumber, fileItem.AttemptNumber);
        Assert.Equal(fixture.Submission.IsLate, fileItem.IsLate);
        Assert.Equal(fixture.SubmissionFile.Id, fileItem.PrimaryFileId);
        var workItemsError = await Assert.ThrowsAsync<ApiException>(() => fixture.QuizGrades.GetWorkItemsAsync(
            null,
            1,
            100,
            fixture.TeacherId,
            "org-b",
            default));
        Assert.Equal(403, workItemsError.StatusCode);
        var error = await Assert.ThrowsAsync<ApiException>(() => fixture.QuizGrades.GetAsync(
            fixture.Attempt.Id,
            fixture.TeacherId,
            "org-b",
            default));
        Assert.Equal(403, error.StatusCode);
    }

    [Fact]
    public async Task GradingWorkItems_MixedHundredEach_PaginatesAndUsesBoundedDatabaseCommands()
    {
        var commands = new CountingCommandInterceptor();
        await using var fixture = await Fixture.CreateAsync(commands);
        await fixture.SeedAdditionalWorkItemsAsync(99);
        var staffId = await fixture.CreatePeerStaffAsync();
        fixture.Db.ChangeTracker.Clear();
        commands.Reset();

        var stopwatch = Stopwatch.StartNew();
        var page = await fixture.QuizGrades.GetWorkItemsAsync(
            null,
            1,
            100,
            staffId,
            "org-a",
            default);
        stopwatch.Stop();
        output.WriteLine($"WORK_ITEMS_MEASUREMENT commands={commands.Count}; elapsed_ms={stopwatch.ElapsedMilliseconds}; total={page.TotalCount}; returned={page.Items.Count}");

        Assert.Equal(200, page.TotalCount);
        Assert.Equal(100, page.Items.Count);
        Assert.Equal(100, page.Items.Select(x => x.Id).Distinct().Count());
        Assert.Equal(3, commands.Count);
        Assert.True(
            commands.Count <= 3,
            $"Expected at most 3 database commands for 200 work items, but observed {commands.Count} in {stopwatch.ElapsedMilliseconds} ms.");

        commands.Reset();
        var firstPageAgain = await fixture.QuizGrades.GetWorkItemsAsync(
            null, 1, 100, staffId, "org-a", default);
        Assert.Equal(3, commands.Count);
        commands.Reset();
        var secondPage = await fixture.QuizGrades.GetWorkItemsAsync(
            null, 2, 100, staffId, "org-a", default);
        Assert.Equal(3, commands.Count);
        Assert.Equal(page.Items.Select(x => x.Id), firstPageAgain.Items.Select(x => x.Id));

        var allItems = page.Items.Concat(secondPage.Items).ToList();
        Assert.Equal(200, allItems.Select(x => x.Id).Distinct().Count());
        Assert.Equal(100, allItems.Count(x => x.Type == GradingWorkItemType.FileSubmission));
        Assert.Equal(100, allItems.Count(x => x.Type == GradingWorkItemType.QuizAttempt));
        Assert.Equal(
            allItems.Select(x => x.Id),
            allItems.OrderByDescending(x => x.SubmittedAtUtc)
                .ThenBy(x => x.StudentCode)
                .ThenBy(x => x.Type)
                .ThenBy(x => x.Id)
                .Select(x => x.Id));

        var otherOrganizationAdmin = await fixture.CreatePeerStaffAsync("org-b", UserRole.Admin);
        var hidden = await fixture.QuizGrades.GetWorkItemsAsync(
            null, 1, 100, otherOrganizationAdmin, "org-b", default);
        Assert.Empty(hidden.Items);
        Assert.Equal(0, hidden.TotalCount);

        var student = await fixture.CreatePeerStaffAsync("org-a", UserRole.Student);
        var forbidden = await Assert.ThrowsAsync<ApiException>(() => fixture.QuizGrades.GetWorkItemsAsync(
            null, 1, 100, student, "org-a", default));
        Assert.Equal(403, forbidden.StatusCode);
    }

    [Fact]
    public async Task GradingWorkItems_EmptyAndStatusFilteredQueuesAreAuthoritative()
    {
        await using var fixture = await Fixture.CreateAsync();

        var notGraded = await fixture.QuizGrades.GetWorkItemsAsync(
            GradingStatus.NotGraded, 1, 100, fixture.TeacherId, "org-a", default);
        Assert.Equal(fixture.Submission.Id, Assert.Single(notGraded.Items).Id);
        var graded = await fixture.QuizGrades.GetWorkItemsAsync(
            GradingStatus.Graded, 1, 100, fixture.TeacherId, "org-a", default);
        Assert.Equal(fixture.Attempt.Id, Assert.Single(graded.Items).Id);

        fixture.Submission.IsOfficial = false;
        fixture.Attempt.Status = QuizAttemptStatus.InProgress;
        await fixture.Db.SaveChangesAsync();
        var empty = await fixture.QuizGrades.GetWorkItemsAsync(
            null, 1, 100, fixture.TeacherId, "org-a", default);
        Assert.Empty(empty.Items);
        Assert.Equal(0, empty.TotalCount);
    }

    [Fact]
    public async Task ArchivePreview_IsReadOnlyEscapesHtmlAndRejectsTraversalAndBombs()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = new SubmissionPreviewService(fixture.Db, fixture.Paths);
        fixture.WriteArchive(("answer.txt", "hello"), ("page.html", "<script>alert(1)</script>"));

        var manifest = await service.GetManifestAsync(
            fixture.Submission.Id,
            fixture.SubmissionFile.Id,
            "org-a",
            default);
        Assert.True(manifest.IsArchive);
        Assert.Equal(2, manifest.Entries.Count);
        var text = await service.GetPreviewAsync(
            fixture.Submission.Id,
            fixture.SubmissionFile.Id,
            "answer.txt",
            "org-a",
            default);
        Assert.Equal("hello", text.Content);
        var html = await service.GetPreviewAsync(
            fixture.Submission.Id,
            fixture.SubmissionFile.Id,
            "page.html",
            "org-a",
            default);
        Assert.DoesNotContain("<script>", html.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html.Content, StringComparison.OrdinalIgnoreCase);

        fixture.WriteArchive(("../escape.txt", "blocked"));
        await Assert.ThrowsAsync<ApiException>(() => service.GetManifestAsync(
            fixture.Submission.Id,
            fixture.SubmissionFile.Id,
            "org-a",
            default));

        fixture.WriteArchiveBytes("bomb.txt", new byte[3 * 1024 * 1024]);
        await Assert.ThrowsAsync<ApiException>(() => service.GetManifestAsync(
            fixture.Submission.Id,
            fixture.SubmissionFile.Id,
            "org-a",
            default));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly string archivePath;

        private Fixture(
            SqliteConnection connection,
            AppDbContext db,
            TestPaths paths,
            Guid teacherId,
            SessionParticipant participant,
            QuizAttempt attempt,
            Submission submission,
            SubmissionFile submissionFile,
            RecordingRealtime realtime)
        {
            this.connection = connection;
            Db = db;
            Paths = paths;
            TeacherId = teacherId;
            Participant = participant;
            Attempt = attempt;
            Submission = submission;
            SubmissionFile = submissionFile;
            Realtime = realtime;
            archivePath = Path.Combine(paths.RootPath, submissionFile.RelativePath);
            QuizGrades = new(
                db,
                new AuditService(db, new HttpContextAccessor()),
                new OutboxService(db));
        }

        public AppDbContext Db { get; }
        public TestPaths Paths { get; }
        public Guid TeacherId { get; }
        public SessionParticipant Participant { get; }
        public QuizAttempt Attempt { get; }
        public Submission Submission { get; }
        public SubmissionFile SubmissionFile { get; }
        public RecordingRealtime Realtime { get; }
        public QuizGradingService QuizGrades { get; }

        public static async Task<Fixture> CreateAsync(DbCommandInterceptor? interceptor = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection);
            if (interceptor is not null)
                options.AddInterceptors(interceptor);
            var db = new AppDbContext(options.Options);
            await db.Database.EnsureCreatedAsync();
            var teacher = new User
            {
                Username = "teacher",
                DisplayName = "Teacher",
                Role = UserRole.Teacher,
                OrganizationId = "org-a"
            };
            var exam = new Exam
            {
                Title = "Unified",
                Subject = "Test",
                DurationMinutes = 30,
                DeliveryType = ExamDeliveryType.MultipleChoice,
                CreatedBy = teacher.Id
            };
            var question = new QuizQuestion
            {
                Exam = exam,
                ExamId = exam.Id,
                Version = 1,
                Order = 1,
                Text = "2 + 2?",
                Points = 10m
            };
            var wrong = new QuizChoice { Question = question, QuestionId = question.Id, Order = 1, Text = "3" };
            var correct = new QuizChoice { Question = question, QuestionId = question.Id, Order = 2, Text = "4", IsCorrect = true };
            question.Choices.Add(wrong);
            question.Choices.Add(correct);
            var session = new ExamSession
            {
                Exam = exam,
                ExamId = exam.Id,
                RoomCode = "GRADE1",
                Status = SessionStatus.Finished,
                DeliveryTypeSnapshot = ExamDeliveryType.MultipleChoice,
                ExamVersionSnapshot = 1,
                QuizResultPolicySnapshot = QuizResultPolicy.Hidden
            };
            var participant = new SessionParticipant
            {
                Session = session,
                SessionId = session.Id,
                StudentCode = "S1",
                DisplayName = "Student",
                DeviceId = "device",
                MachineName = "machine",
                AppVersion = "1",
                Status = ParticipantStatus.Approved
            };
            var snapshot = new List<QuizQuestionDto>
            {
                new(question.Id, question.Text, 1, 10m, false,
                    [new(wrong.Id, wrong.Text, 1), new(correct.Id, correct.Text, 2)])
            };
            var attempt = new QuizAttempt
            {
                Session = session,
                SessionId = session.Id,
                Participant = participant,
                ParticipantId = participant.Id,
                ExamVersion = 1,
                Status = QuizAttemptStatus.Finalized,
                StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-20),
                DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(10),
                FinalizedAtUtc = DateTimeOffset.UtcNow,
                AutoScore = 8m,
                Score = 8m,
                MaxScore = 10m,
                GradingStatus = GradingStatus.Graded,
                GradedAtUtc = DateTimeOffset.UtcNow,
                ResultPolicySnapshot = QuizResultPolicy.Hidden,
                SnapshotJson = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            };
            attempt.Answers.Add(new QuizAnswer
            {
                Attempt = attempt,
                AttemptId = attempt.Id,
                Question = question,
                QuestionId = question.Id,
                ChoiceIdsJson = JsonSerializer.Serialize(new[] { correct.Id }),
                Revision = 1,
                ClientUpdatedAtUtc = DateTimeOffset.UtcNow
            });
            var submission = new Submission
            {
                Session = session,
                SessionId = session.Id,
                Participant = participant,
                ParticipantId = participant.Id,
                AttemptNumber = 1,
                IdempotencyKey = "file-1",
                Status = SubmissionStatus.Submitted,
                ClientSubmittedAtUtc = DateTimeOffset.UtcNow,
                DeadlineUtc = DateTimeOffset.UtcNow,
                IsOfficial = true
            };
            var paths = new TestPaths(Path.Combine(Path.GetTempPath(), "ExamTransfer.UnifiedGrading", Guid.NewGuid().ToString("N")));
            paths.EnsureCreated();
            var file = new SubmissionFile
            {
                Submission = submission,
                SubmissionId = submission.Id,
                ClientFileId = "archive",
                OriginalName = "submission.zip",
                StoredName = "submission.zip",
                RelativePath = Path.Combine("files", "submission.zip"),
                SizeBytes = 1,
                TransferStatus = TransferStatus.Completed
            };
            submission.Files.Add(file);
            db.AddRange(teacher, exam, question, wrong, correct, session, participant, attempt, submission);
            await db.SaveChangesAsync();
            return new(connection, db, paths, teacher.Id, participant, attempt, submission, file, new());
        }

        public async Task<Guid> CreatePeerStaffAsync(
            string organizationId = "org-a",
            UserRole role = UserRole.Admin)
        {
            var staff = new User
            {
                Username = $"staff-{Guid.NewGuid():N}",
                DisplayName = "Peer Staff",
                Role = role,
                OrganizationId = organizationId
            };
            Db.Add(staff);
            await Db.SaveChangesAsync();
            return staff.Id;
        }

        public async Task SeedAdditionalWorkItemsAsync(int count)
        {
            for (var index = 0; index < count; index++)
            {
                var participant = new SessionParticipant
                {
                    SessionId = Participant.SessionId,
                    StudentCode = $"S{index + 2:D3}",
                    DisplayName = $"Student {index + 2:D3}",
                    DeviceId = $"device-{index + 2:D3}",
                    MachineName = "machine",
                    AppVersion = "1",
                    Status = ParticipantStatus.Approved
                };
                var submittedAt = DateTimeOffset.UtcNow.AddSeconds(-index - 1);
                var submission = new Submission
                {
                    SessionId = Participant.SessionId,
                    Participant = participant,
                    ParticipantId = participant.Id,
                    AttemptNumber = 1,
                    IdempotencyKey = $"file-{index + 2:D3}",
                    Status = SubmissionStatus.Submitted,
                    ClientSubmittedAtUtc = submittedAt,
                    ServerReceivedAtUtc = submittedAt,
                    DeadlineUtc = submittedAt.AddMinutes(1),
                    IsOfficial = true
                };
                submission.Files.Add(new SubmissionFile
                {
                    Submission = submission,
                    SubmissionId = submission.Id,
                    ClientFileId = $"file-{index + 2:D3}",
                    OriginalName = $"submission-{index + 2:D3}.txt",
                    StoredName = $"submission-{index + 2:D3}.txt",
                    RelativePath = Path.Combine("files", $"submission-{index + 2:D3}.txt"),
                    SizeBytes = index + 1,
                    TransferStatus = TransferStatus.Completed
                });
                var attempt = new QuizAttempt
                {
                    SessionId = Participant.SessionId,
                    Participant = participant,
                    ParticipantId = participant.Id,
                    AttemptNumber = 1,
                    ExamVersion = 1,
                    Status = QuizAttemptStatus.Finalized,
                    StartedAtUtc = submittedAt.AddMinutes(-10),
                    DeadlineUtc = submittedAt.AddMinutes(1),
                    FinalizedAtUtc = submittedAt,
                    AutoScore = 8m,
                    Score = 8m,
                    MaxScore = 10m,
                    GradingStatus = GradingStatus.Graded,
                    GradedAtUtc = submittedAt,
                    ResultPolicySnapshot = QuizResultPolicy.Hidden,
                    SnapshotJson = "[]"
                };
                Db.AddRange(participant, submission, attempt);
            }
            await Db.SaveChangesAsync();
        }

        public void WriteArchive(params (string Name, string Content)[] entries)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            using var archive = new ZipArchive(File.Create(archivePath), ZipArchiveMode.Create);
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        public void WriteArchiveBytes(string name, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            using var archive = new ZipArchive(File.Create(archivePath), ZipArchiveMode.Create);
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(bytes);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
            if (Directory.Exists(Paths.RootPath))
                Directory.Delete(Paths.RootPath, recursive: true);
        }
    }

    private sealed class CountingCommandInterceptor : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public void Reset() => Count = 0;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Count++;
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingRealtime : IRealtimePublisher
    {
        public List<string> Events { get; } = [];
        public Task PublishSessionAsync<T>(Guid sessionId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task PublishParticipantAsync<T>(Guid sessionId, Guid participantId, string eventName, long sequence, T payload, CancellationToken cancellationToken = default)
        {
            Events.Add(eventName);
            return Task.CompletedTask;
        }
    }

    public sealed class TestPaths(string root) : IStoragePaths
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
        public void EnsureCreated()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            Directory.CreateDirectory(BackupRoot);
            Directory.CreateDirectory(ExportRoot);
            Directory.CreateDirectory(TemporaryRoot);
        }
    }
}
