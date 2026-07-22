using System.Text;
using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Services;
using ExamTransfer.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class QuizWorkflowTests
{
    [Fact]
    public async Task ImportSyncFinalize_IsResumableIdempotentAndServerGraded()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var exam = new Exam { Title = "Quiz", Subject = "Test", DurationMinutes = 30, Status = ExamStatus.Draft };
        db.ExamsSet.Add(exam);
        await db.SaveChangesAsync();
        var service = new QuizService(db, new OutboxService(db));
        var import = new QuizImportDocument(
        [
            new QuizImportQuestion("2 + 2?", 2, false, ["3", "4", "5"], [1]),
            new QuizImportQuestion("Số chẵn", 3, true, ["2", "3", "4"], [0, 2])
        ]);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(import, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var imported = await service.ImportAsync(exam.Id, new QuizImportFileRequest("quiz.json", Convert.ToBase64String(bytes)), default);
        Assert.Equal(2, imported.QuestionCount);

        exam.Status = ExamStatus.Published;
        var session = new ExamSession { ExamId = exam.Id, Exam = exam, RoomCode = "QUIZ01", Status = SessionStatus.InProgress, StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var participant = new SessionParticipant { Session = session, SessionId = session.Id, StudentCode = "S1", DisplayName = "Student", DeviceId = "d", MachineName = "m", AppVersion = "1", Status = ParticipantStatus.Approved };
        db.ExamSessionsSet.Add(session); db.SessionParticipantsSet.Add(participant);
        await db.SaveChangesAsync();

        var attempt = await service.StartOrGetAttemptAsync(session.Id, participant.Id, default);
        Assert.Equal(2, attempt.Questions.Count);
        Assert.DoesNotContain("correct", (await db.QuizAttemptsSet.SingleAsync()).SnapshotJson, StringComparison.OrdinalIgnoreCase);
        var q1 = attempt.Questions[0]; var q2 = attempt.Questions[1];
        await service.SyncAnswersAsync(attempt.Id, participant.Id, new([
            new(q1.Id, [q1.Choices[1].Id], 2, DateTimeOffset.UtcNow),
            new(q2.Id, [q2.Choices[0].Id, q2.Choices[2].Id], 1, DateTimeOffset.UtcNow)
        ]), default);
        var stale = await service.SyncAnswersAsync(attempt.Id, participant.Id, new([
            new(q1.Id, [q1.Choices[0].Id], 1, DateTimeOffset.UtcNow)
        ]), default);
        Assert.Equal(q1.Choices[1].Id, stale.Answers.Single(x => x.QuestionId == q1.Id).ChoiceIds.Single());

        var finalized = await service.FinalizeAsync(attempt.Id, participant.Id, new("final-1", DateTimeOffset.UtcNow), default);
        var repeated = await service.FinalizeAsync(attempt.Id, participant.Id, new("final-1", DateTimeOffset.UtcNow), default);
        Assert.Equal(5, finalized.Score);
        Assert.Equal(finalized.Score, repeated.Score);
        Assert.Equal(QuizAttemptStatus.Finalized, finalized.Status);
        await Assert.ThrowsAsync<ApiException>(() => service.SyncAnswersAsync(attempt.Id, participant.Id, new([]), default));
        await Assert.ThrowsAsync<ApiException>(() => service.FinalizeAsync(attempt.Id, Guid.NewGuid(), new("other", DateTimeOffset.UtcNow), default));
    }
}
