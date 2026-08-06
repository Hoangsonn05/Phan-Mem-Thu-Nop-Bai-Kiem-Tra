using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Execution.PublicCloud;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class QuizAttemptIntegrityTests
{
    [Fact]
    public async Task NewAttempt_RequiresValidQuestionGraph()
    {
        await using var fixture = await Fixture.CreateAsync(withQuestions: false);

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.Service.StartOrGetAttemptAsync(
                fixture.Session.Id,
                fixture.Participant.Id,
                CancellationToken.None));

        Assert.Equal(ErrorCodes.QuizHasNoQuestions, error.Code);
        Assert.Empty(await fixture.Db.QuizAttemptsSet.ToListAsync());
    }

    [Fact]
    public async Task NewAttempt_ContainsValidatedStudentSafeSnapshot()
    {
        await using var fixture = await Fixture.CreateAsync();

        var attempt = await fixture.Service.StartOrGetAttemptAsync(
            fixture.Session.Id,
            fixture.Participant.Id,
            CancellationToken.None);

        Assert.Equal(2, attempt.Questions.Count);
        Assert.Equal(10m, attempt.Questions.Sum(x => x.Points));
        Assert.All(attempt.Questions, question => Assert.Equal(2, question.Choices.Count));
        Assert.Equal(fixture.Questions.OrderBy(x => x.Order).Select(x => x.Id), attempt.Questions.Select(x => x.Id));
        Assert.All(attempt.Questions, question =>
        {
            var canonical = fixture.Questions.Single(x => x.Id == question.Id);
            Assert.Equal(canonical.Choices.OrderBy(x => x.Order).Select(x => x.Id), question.Choices.Select(x => x.Id));
        });
        var persisted = await fixture.Db.QuizAttemptsSet.SingleAsync();
        Assert.DoesNotContain("correct", persisted.SnapshotJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShuffleEnabled_CreatesStableNonCanonicalStudentSnapshot()
    {
        await using var fixture = await Fixture.CreateAsync(questionCount: 8, choiceCount: 8);
        fixture.Session.QuizShuffleEnabledSnapshot = true;
        await fixture.Db.SaveChangesAsync();

        var first = await fixture.Service.StartOrGetAttemptAsync(
            fixture.Session.Id,
            fixture.Participant.Id,
            CancellationToken.None);
        var reloaded = await fixture.Service.GetAttemptAsync(
            fixture.Session.Id,
            fixture.Participant.Id,
            CancellationToken.None);

        var canonicalQuestionIds = fixture.Questions
            .OrderBy(question => question.Order)
            .Select(question => question.Id)
            .ToArray();
        Assert.False(canonicalQuestionIds.SequenceEqual(first.Questions.Select(question => question.Id)));
        Assert.Equal(
            first.Questions.Select(question => question.Id),
            Assert.IsType<QuizAttemptDto>(reloaded).Questions.Select(question => question.Id));
        Assert.Equal(Enumerable.Range(1, 8), first.Questions.Select(question => question.Order));
        Assert.Equal(fixture.Questions.Select(question => question.Id).Order(), first.Questions.Select(question => question.Id).Order());
        Assert.Equal(fixture.Questions.Sum(question => question.Points), first.Questions.Sum(question => question.Points));
        Assert.All(first.Questions, question =>
        {
            var canonical = fixture.Questions.Single(row => row.Id == question.Id);
            Assert.Equal(canonical.Choices.Select(choice => choice.Id).Order(), question.Choices.Select(choice => choice.Id).Order());
            Assert.Equal(Enumerable.Range(1, question.Choices.Count), question.Choices.Select(choice => choice.Order));
        });
        Assert.Contains(first.Questions, question =>
        {
            var canonical = fixture.Questions.Single(row => row.Id == question.Id)
                .Choices.OrderBy(choice => choice.Order).Select(choice => choice.Id);
            return !canonical.SequenceEqual(question.Choices.Select(choice => choice.Id));
        });
        Assert.DoesNotContain("correct", (await fixture.Db.QuizAttemptsSet.SingleAsync()).SnapshotJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShuffleKeysAndPermutations_AreParticipantScopedAndVersioned()
    {
        await using var fixture = await Fixture.CreateAsync(questionCount: 8, choiceCount: 8);
        fixture.Session.QuizShuffleEnabledSnapshot = true;
        var secondParticipant = await fixture.AddParticipantAsync("S-2");
        await fixture.Db.SaveChangesAsync();

        var first = await fixture.Service.StartOrGetAttemptAsync(
            fixture.Session.Id,
            fixture.Participant.Id,
            CancellationToken.None);
        var second = await fixture.Service.StartOrGetAttemptAsync(
            fixture.Session.Id,
            secondParticipant.Id,
            CancellationToken.None);
        var questionId = fixture.Questions[0].Id;
        var choiceId = fixture.Questions[0].Choices.First().Id;

        Assert.Equal("quiz-shuffle-v1", QuizDeterministicShuffle.AlgorithmVersion);
        Assert.NotEqual(
            QuizDeterministicShuffle.QuestionSortKey(fixture.Session.Id, fixture.Participant.Id, fixture.Session.ExamVersionSnapshot, questionId),
            QuizDeterministicShuffle.QuestionSortKey(fixture.Session.Id, secondParticipant.Id, fixture.Session.ExamVersionSnapshot, questionId));
        Assert.NotEqual(
            QuizDeterministicShuffle.ChoiceSortKey(fixture.Session.Id, fixture.Participant.Id, fixture.Session.ExamVersionSnapshot, questionId, choiceId),
            QuizDeterministicShuffle.ChoiceSortKey(fixture.Session.Id, secondParticipant.Id, fixture.Session.ExamVersionSnapshot, questionId, choiceId));
        Assert.False(first.Questions.Select(question => question.Id).SequenceEqual(second.Questions.Select(question => question.Id)));
    }

    [Fact]
    public async Task ShuffleEnabled_GradesByQuestionAndChoiceIds()
    {
        await using var fixture = await Fixture.CreateAsync(questionCount: 8, choiceCount: 8);
        fixture.Session.QuizShuffleEnabledSnapshot = true;
        await fixture.Db.SaveChangesAsync();
        var attempt = await fixture.Service.StartOrGetAttemptAsync(
            fixture.Session.Id,
            fixture.Participant.Id,
            CancellationToken.None);
        var correctChoiceByQuestion = fixture.Questions.ToDictionary(
            question => question.Id,
            question => question.Choices.Single(choice => choice.IsCorrect).Id);

        await fixture.Service.SyncAnswersAsync(
            attempt.Id,
            fixture.Participant.Id,
            new(attempt.Questions.Select((question, index) => new QuizAnswerDto(
                question.Id,
                [correctChoiceByQuestion[question.Id]],
                index + 1,
                DateTimeOffset.UtcNow)).ToList()),
            CancellationToken.None);
        var reloaded = await fixture.Service.GetAttemptAsync(
            fixture.Session.Id,
            fixture.Participant.Id,
            CancellationToken.None);
        Assert.All(
            Assert.IsType<QuizAttemptDto>(reloaded).Answers,
            answer => Assert.Equal(correctChoiceByQuestion[answer.QuestionId], answer.ChoiceIds.Single()));
        await fixture.Service.FinalizeAsync(
            attempt.Id,
            fixture.Participant.Id,
            new("shuffle-final", DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(10m, (await fixture.Db.QuizAttemptsSet.SingleAsync()).Score);
    }

    [Fact]
    public async Task ExistingValidAttempt_IsReturnedWithoutRebuildOrTimeChanges()
    {
        await using var fixture = await Fixture.CreateAsync();
        var existing = await fixture.AddAttemptAsync(fixture.ValidSnapshotJson);
        var snapshot = existing.SnapshotJson;
        var startedAt = existing.StartedAtUtc;
        var deadline = existing.DeadlineUtc;

        var result = await fixture.Service.StartOrGetAttemptAsync(
            fixture.Session.Id,
            fixture.Participant.Id,
            CancellationToken.None);

        Assert.Equal(existing.Id, result.Id);
        Assert.Equal(2, result.Questions.Count);
        fixture.Db.ChangeTracker.Clear();
        var persisted = await fixture.Db.QuizAttemptsSet.SingleAsync();
        Assert.Equal(snapshot, persisted.SnapshotJson);
        Assert.Equal(startedAt, persisted.StartedAtUtc);
        Assert.Equal(deadline, persisted.DeadlineUtc);
    }

    [Fact]
    public async Task ExistingSnapshot_IsNotReshuffledWhenSessionFlagChanges()
    {
        await using var fixture = await Fixture.CreateAsync(questionCount: 8, choiceCount: 8);
        var existing = await fixture.AddAttemptAsync(fixture.ValidSnapshotJson);
        var snapshot = existing.SnapshotJson;
        fixture.Session.QuizShuffleEnabledSnapshot = true;
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.StartOrGetAttemptAsync(
            fixture.Session.Id,
            fixture.Participant.Id,
            CancellationToken.None);

        Assert.Equal(fixture.Questions.OrderBy(x => x.Order).Select(x => x.Id), result.Questions.Select(x => x.Id));
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(snapshot, (await fixture.Db.QuizAttemptsSet.SingleAsync()).SnapshotJson);
    }

    [Fact]
    public async Task ShuffleEnabled_RepairRecreatesTheDeterministicPermutation()
    {
        await using var fixture = await Fixture.CreateAsync(questionCount: 8, choiceCount: 8);
        fixture.Session.QuizShuffleEnabledSnapshot = true;
        var existing = await fixture.AddAttemptAsync("[]");
        var expected = QuizDeterministicShuffle.BuildSnapshot(
            fixture.Questions,
            fixture.Session.Id,
            fixture.Participant.Id,
            fixture.Session.ExamVersionSnapshot);

        var repaired = await fixture.Service.StartOrGetAttemptAsync(
            fixture.Session.Id,
            fixture.Participant.Id,
            CancellationToken.None);
        var reloaded = await fixture.Service.GetAttemptAsync(
            fixture.Session.Id,
            fixture.Participant.Id,
            CancellationToken.None);

        Assert.Equal(existing.Id, repaired.Id);
        Assert.Equal(expected.Select(x => x.Id), repaired.Questions.Select(x => x.Id));
        Assert.Equal(repaired.Questions.Select(x => x.Id), Assert.IsType<QuizAttemptDto>(reloaded).Questions.Select(x => x.Id));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{malformed")]
    public async Task EmptyOrMalformedAttempt_RepairsSameAttemptAndPreservesTimes(string snapshotJson)
    {
        await using var fixture = await Fixture.CreateAsync();
        var existing = await fixture.AddAttemptAsync(snapshotJson);
        var startedAt = existing.StartedAtUtc;
        var deadline = existing.DeadlineUtc;

        var result = await fixture.Service.StartOrGetAttemptAsync(
            fixture.Session.Id,
            fixture.Participant.Id,
            CancellationToken.None);

        Assert.Equal(existing.Id, result.Id);
        Assert.Equal(2, result.Questions.Count);
        fixture.Db.ChangeTracker.Clear();
        var persisted = await fixture.Db.QuizAttemptsSet.SingleAsync();
        Assert.NotEqual(snapshotJson, persisted.SnapshotJson);
        Assert.Equal(startedAt, persisted.StartedAtUtc);
        Assert.Equal(deadline, persisted.DeadlineUtc);
        Assert.Single(await fixture.Db.QuizAttemptsSet.ToListAsync());
    }

    [Fact]
    public async Task EmptyAttemptWithAnswer_IsRejectedWithoutMutation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var existing = await fixture.AddAttemptAsync("[]");
        fixture.Db.QuizAnswersSet.Add(new QuizAnswer
        {
            AttemptId = existing.Id,
            QuestionId = fixture.Questions[0].Id,
            ChoiceIdsJson = "[]",
            Revision = 1,
            ClientUpdatedAtUtc = DateTimeOffset.UtcNow
        });
        await fixture.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.Service.StartOrGetAttemptAsync(
                fixture.Session.Id,
                fixture.Participant.Id,
                CancellationToken.None));

        Assert.Equal(ErrorCodes.QuizAttemptSnapshotInvalid, error.Code);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal("[]", (await fixture.Db.QuizAttemptsSet.SingleAsync()).SnapshotJson);
        Assert.Single(await fixture.Db.QuizAnswersSet.ToListAsync());
    }

    [Fact]
    public async Task EmptyFinalizedAttempt_IsRejectedWithoutMutation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var existing = await fixture.AddAttemptAsync("[]");
        existing.Status = QuizAttemptStatus.Finalized;
        existing.FinalizedAtUtc = DateTimeOffset.UtcNow;
        existing.GradingStatus = GradingStatus.Graded;
        existing.AutoScore = 0;
        existing.Score = 0;
        await fixture.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.Service.StartOrGetAttemptAsync(
                fixture.Session.Id,
                fixture.Participant.Id,
                CancellationToken.None));

        Assert.Equal(ErrorCodes.QuizAttemptSnapshotInvalid, error.Code);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal("[]", (await fixture.Db.QuizAttemptsSet.SingleAsync()).SnapshotJson);
    }

    [Fact]
    public async Task EmptyAttemptWithVersionMismatch_IsRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var existing = await fixture.AddAttemptAsync("[]");
        existing.ExamVersion = fixture.Session.ExamVersionSnapshot + 1;
        await fixture.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.Service.StartOrGetAttemptAsync(
                fixture.Session.Id,
                fixture.Participant.Id,
                CancellationToken.None));

        Assert.Equal(ErrorCodes.QuizAttemptSnapshotInvalid, error.Code);
    }

    [Fact]
    public async Task ReadGate_RejectsEmptyAttemptInsteadOfReturningEmptyQuestions()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAttemptAsync("[]");

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.Service.GetAttemptAsync(
                fixture.Session.Id,
                fixture.Participant.Id,
                CancellationToken.None));

        Assert.Equal(ErrorCodes.QuizAttemptSnapshotInvalid, error.Code);
    }

    [Fact]
    public async Task ConcurrentStart_CreatesOneAttemptAndReturnsSameId()
    {
        var databaseName = $"quiz-race-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared";
        await using var fixture = await Fixture.CreateAsync(connectionString: connectionString);
        fixture.Session.QuizShuffleEnabledSnapshot = true;
        await fixture.Db.SaveChangesAsync();
        await using var firstConnection = new SqliteConnection(connectionString);
        await using var secondConnection = new SqliteConnection(connectionString);
        await firstConnection.OpenAsync();
        await secondConnection.OpenAsync();
        await using var firstDb = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(firstConnection).Options);
        await using var secondDb = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(secondConnection).Options);
        var firstService = new QuizService(
            firstDb,
            new QuizProjectionOutbox(new OutboxService(firstDb)));
        var secondService = new QuizService(
            secondDb,
            new QuizProjectionOutbox(new OutboxService(secondDb)));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<QuizAttemptDto> StartAsync(QuizService service)
        {
            await release.Task;
            return await service.StartOrGetAttemptAsync(
                fixture.Session.Id,
                fixture.Participant.Id,
                CancellationToken.None);
        }

        var first = StartAsync(firstService);
        var second = StartAsync(secondService);
        release.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(results[0].Id, results[1].Id);
        Assert.Equal(
            results[0].Questions.Select(question => question.Id),
            results[1].Questions.Select(question => question.Id));
        fixture.Db.ChangeTracker.Clear();
        Assert.Single(await fixture.Db.QuizAttemptsSet.ToListAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private Fixture(
            SqliteConnection connection,
            AppDbContext db,
            QuizService service,
            ExamSession session,
            SessionParticipant participant,
            IReadOnlyList<QuizQuestion> questions)
        {
            this.connection = connection;
            Db = db;
            Service = service;
            Session = session;
            Participant = participant;
            Questions = questions;
        }

        public AppDbContext Db { get; }
        public QuizService Service { get; }
        public ExamSession Session { get; }
        public SessionParticipant Participant { get; }
        public IReadOnlyList<QuizQuestion> Questions { get; }
        public string ValidSnapshotJson => System.Text.Json.JsonSerializer.Serialize(
            Questions.OrderBy(x => x.Order).Select(x => new QuizQuestionDto(
                x.Id,
                x.Text,
                x.Order,
                x.Points,
                x.Multiple,
                x.Choices.OrderBy(choice => choice.Order)
                    .Select(choice => new QuizChoiceDto(choice.Id, choice.Text, choice.Order))
                    .ToList())).ToList(),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        public static async Task<Fixture> CreateAsync(
            bool withQuestions = true,
            int questionCount = 2,
            int choiceCount = 2,
            string connectionString = "Data Source=:memory:")
        {
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var exam = new Exam
            {
                Title = "Integrity quiz",
                Subject = "Tests",
                DurationMinutes = 30,
                Status = ExamStatus.Published,
                DeliveryType = ExamDeliveryType.MultipleChoice,
                SupervisionMode = SupervisionMode.Standard,
                QuizResultPolicy = QuizResultPolicy.Hidden
            };
            var questions = new List<QuizQuestion>();
            if (withQuestions)
            {
                for (var order = 1; order <= questionCount; order++)
                    questions.Add(Question(exam, order, $"Question {order}", 10m / questionCount, choiceCount));
            }
            var session = new ExamSession
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000001"),
                Exam = exam,
                ExamId = exam.Id,
                RoomCode = $"Q{Guid.NewGuid():N}"[..8],
                Status = SessionStatus.InProgress,
                StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                AccessMode = SessionAccessMode.LanOnly,
                DeliveryTypeSnapshot = ExamDeliveryType.MultipleChoice,
                SupervisionModeSnapshot = SupervisionMode.Standard,
                QuizResultPolicySnapshot = QuizResultPolicy.Hidden,
                ExamVersionSnapshot = exam.Version
            };
            var participant = new SessionParticipant
            {
                Id = Guid.Parse("20000000-0000-0000-0000-000000000001"),
                Session = session,
                SessionId = session.Id,
                StudentCode = "S-1",
                DisplayName = "Student",
                DeviceId = "device",
                MachineName = "machine",
                AppVersion = "test",
                Status = ParticipantStatus.Approved
            };
            db.AddRange(exam, session, participant);
            db.QuizQuestionsSet.AddRange(questions);
            db.ControlPoliciesSet.Add(new ControlPolicy
            {
                SessionId = session.Id,
                Version = 1,
                Status = PolicyApplyStatus.Applied
            });
            db.DevicePolicyStatusesSet.Add(new DevicePolicyStatus
            {
                SessionId = session.Id,
                ParticipantId = participant.Id,
                PolicyVersion = 1,
                Status = PolicyApplyStatus.Applied
            });
            await db.SaveChangesAsync();
            var service = new QuizService(
                db,
                new QuizProjectionOutbox(new OutboxService(db)));
            return new(connection, db, service, session, participant, questions);
        }

        public async Task<SessionParticipant> AddParticipantAsync(string studentCode)
        {
            var participant = new SessionParticipant
            {
                Id = Guid.Parse("20000000-0000-0000-0000-000000000002"),
                Session = Session,
                SessionId = Session.Id,
                StudentCode = studentCode,
                DisplayName = "Second student",
                DeviceId = "device-2",
                MachineName = "machine-2",
                AppVersion = "test",
                Status = ParticipantStatus.Approved
            };
            Db.SessionParticipantsSet.Add(participant);
            Db.DevicePolicyStatusesSet.Add(new DevicePolicyStatus
            {
                SessionId = Session.Id,
                ParticipantId = participant.Id,
                PolicyVersion = 1,
                Status = PolicyApplyStatus.Applied
            });
            await Db.SaveChangesAsync();
            return participant;
        }

        public async Task<QuizAttempt> AddAttemptAsync(string snapshotJson)
        {
            var attempt = new QuizAttempt
            {
                SessionId = Session.Id,
                ParticipantId = Participant.Id,
                ExamVersion = Session.ExamVersionSnapshot,
                Status = QuizAttemptStatus.InProgress,
                StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(29),
                MaxScore = 10,
                GradingStatus = GradingStatus.InProgress,
                SnapshotJson = snapshotJson,
                ResultPolicySnapshot = QuizResultPolicy.Hidden
            };
            Db.QuizAttemptsSet.Add(attempt);
            await Db.SaveChangesAsync();
            return attempt;
        }

        private static QuizQuestion Question(
            Exam exam,
            int order,
            string text,
            decimal points = 5m,
            int choiceCount = 2)
        {
            var question = new QuizQuestion
            {
                Id = Guid.Parse($"00000000-0000-0000-{order:X4}-000000000000"),
                Exam = exam,
                ExamId = exam.Id,
                Version = exam.Version,
                Order = order,
                Text = text,
                Points = points,
                Multiple = false
            };
            for (var choiceOrder = 1; choiceOrder <= choiceCount; choiceOrder++)
            {
                question.Choices.Add(new QuizChoice
                {
                    Id = Guid.Parse($"00000000-0000-0001-{order:X4}-{choiceOrder:X12}"),
                    Question = question,
                    QuestionId = question.Id,
                    Order = choiceOrder,
                    Text = $"Option {choiceOrder}",
                    IsCorrect = choiceOrder == 1
                });
            }
            return question;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
