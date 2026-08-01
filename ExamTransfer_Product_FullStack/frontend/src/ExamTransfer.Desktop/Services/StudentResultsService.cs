using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Models;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Services;

public interface IStudentResultsService
{
    Task<IReadOnlyList<StudentReturnedResult>> GetReturnedResultsAsync(CancellationToken cancellationToken);
    Task DownloadAttachmentAsync(
        StudentResultAttachment attachment,
        string destinationPath,
        CancellationToken cancellationToken);
}

public sealed class StudentResultsService(
    IBackendClient backend,
    SupabasePublicCloudClient publicCloud,
    StudentSessionState session) : IStudentResultsService
{
    public async Task<IReadOnlyList<StudentReturnedResult>> GetReturnedResultsAsync(
        CancellationToken cancellationToken)
    {
        if (!session.SessionId.HasValue || !session.ParticipantId.HasValue)
            return [];

        return session.DeliveryType == ExamDeliveryType.MultipleChoice
            ? await GetQuizResultAsync(cancellationToken)
            : await GetFileResultAsync(cancellationToken);
    }

    public Task DownloadAttachmentAsync(
        StudentResultAttachment attachment,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(attachment.DownloadPath))
            throw new InvalidOperationException("Attachment chưa có đường dẫn tải authoritative.");
        return backend.DownloadFileAsync(
            attachment.DownloadPath.TrimStart('/'),
            destinationPath,
            null,
            cancellationToken);
    }

    private async Task<IReadOnlyList<StudentReturnedResult>> GetQuizResultAsync(
        CancellationToken cancellationToken)
    {
        var attempt = session.CurrentAttempt;
        if (attempt?.Status != QuizAttemptStatus.Finalized)
            return [];

        var review = session.AccessMode == SessionAccessMode.PublicCloud
            ? await publicCloud.GetQuizAttemptReviewAsync(attempt.Id, cancellationToken)
            : ApiGuard.Require(await backend.GetAsync<StudentQuizReviewDto>(
                $"api/v1/student/quiz/attempts/{attempt.Id}/review",
                cancellationToken));

        // CorrectAnswersVisible is the only existing student DTO signal that the
        // teacher has actually returned the result, rather than merely exposing
        // an immediate post-submission score.
        if (!review.CorrectAnswersVisible)
            return [];

        return
        [
            new(
                attempt.Id,
                session.SessionId!.Value,
                session.ParticipantId!.Value,
                string.IsNullOrWhiteSpace(session.ExamTitle) ? "Bài trắc nghiệm" : session.ExamTitle,
                StudentResultKind.Quiz,
                null,
                GradingStatus.Returned,
                review.Score,
                review.MaxScore,
                review.GeneralComment,
                null,
                session.AccessMode,
                review.Questions,
                [])
        ];
    }

    private async Task<IReadOnlyList<StudentReturnedResult>> GetFileResultAsync(
        CancellationToken cancellationToken)
    {
        if (!session.LastSubmissionId.HasValue)
            return [];
        if (session.AccessMode == SessionAccessMode.PublicCloud)
            throw new StudentResultsIntegrationException(
                "PublicCloud chưa cung cấp contract đọc kết quả tự luận/file đã trả.");

        GradeDto grade;
        try
        {
            grade = ApiGuard.Require(await backend.GetAsync<GradeDto>(
                $"api/v1/student/submissions/{session.LastSubmissionId.Value}/grade",
                cancellationToken));
        }
        catch (BackendApiException exception) when (exception.HttpStatusCode == 403)
        {
            return [];
        }
        if (grade.Status != GradingStatus.Returned)
            return [];

        return
        [
            new(
                grade.SubmissionId,
                session.SessionId!.Value,
                session.ParticipantId!.Value,
                string.IsNullOrWhiteSpace(session.ExamTitle) ? "Bài tự luận/file" : session.ExamTitle,
                StudentResultKind.EssayFile,
                null,
                grade.Status,
                grade.Score,
                grade.MaxScore,
                grade.GeneralComment,
                grade.ReturnedAtUtc,
                session.AccessMode,
                [],
                grade.Attachments.Select(attachment => new StudentResultAttachment(
                    attachment.Id,
                    attachment.Name,
                    attachment.SizeBytes,
                    attachment.MimeType,
                    attachment.DownloadUrl ?? string.Empty,
                    grade.SubmissionId)).ToArray())
        ];
    }
}

public sealed class StudentResultsIntegrationException(string message) : Exception(message);
