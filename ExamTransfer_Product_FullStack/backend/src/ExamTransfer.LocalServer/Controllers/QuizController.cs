using ExamTransfer.Application;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ExamTransfer.LocalServer.Controllers;

[Route("api/v1/exams/{examId:guid}/quiz")]
[Authorize(Policy = "TeacherOrAdmin")]
public sealed class QuizAuthoringController(IQuizService quiz) : ApiControllerBase
{
    [HttpPost("import")]
    public async Task<ActionResult<ApiResponse<QuizImportResultDto>>> Import(Guid examId, QuizImportFileRequest request, CancellationToken ct) =>
        Data(await quiz.ImportAsync(examId, request, ct));

    [HttpPost("/api/v1/exams/{examId:guid}/quiz-import/preview")]
    public async Task<ActionResult<ApiResponse<QuizImportPreviewDto>>> Preview(
        Guid examId,
        QuizImportPreviewRequest request,
        CancellationToken ct) =>
        Data(await quiz.PreviewImportAsync(examId, RequiredGuidClaim(ClaimTypes.NameIdentifier), request, ct));

    [HttpPost("/api/v1/exams/{examId:guid}/quiz-import/commit")]
    public async Task<ActionResult<ApiResponse<QuizImportResultDto>>> Commit(
        Guid examId,
        QuizImportCommitRequest request,
        CancellationToken ct) =>
        Data(await quiz.CommitImportAsync(examId, RequiredGuidClaim(ClaimTypes.NameIdentifier), request, ct));
}

[Route("api/v1/sessions/{sessionId:guid}/quiz-attempts")]
[Authorize(Policy = "TeacherOrAdmin")]
public sealed class TeacherQuizMonitoringController(IQuizService quiz) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<TeacherQuizAttemptSummaryDto>>>> List(Guid sessionId, CancellationToken ct) =>
        Data(await quiz.ListTeacherSubmissionsForSessionAsync(
            sessionId,
            RequiredGuidClaim(ClaimTypes.NameIdentifier),
            User.FindFirst("organization_id")?.Value,
            ct));
}

[Route("api/v1/student/quiz")]
[Authorize(Policy = "StudentWithParticipant")]
public sealed class StudentQuizController(IQuizService quiz, IQuizGradingService quizGrades) : ApiControllerBase
{
    [HttpGet("attempts/{attemptId:guid}/review")]
    public async Task<ActionResult<ApiResponse<StudentQuizReviewDto>>> Review(Guid attemptId, CancellationToken ct) =>
        Data(await quizGrades.GetStudentReviewAsync(attemptId, RequiredGuidClaim("participant_id"), ct));

    [HttpGet("sessions/{sessionId:guid}/attempt")]
    public async Task<ActionResult<ApiResponse<QuizAttemptLookupDto>>> Get(Guid sessionId, CancellationToken ct)
    {
        var participantId = RequiredGuidClaim("participant_id");
        EnsureStudentScope(sessionId, participantId);
        return Data(new QuizAttemptLookupDto(await quiz.GetAttemptAsync(sessionId, participantId, ct)));
    }

    [HttpPost("sessions/{sessionId:guid}/attempt")]
    public async Task<ActionResult<ApiResponse<QuizAttemptDto>>> Start(Guid sessionId, CancellationToken ct)
    {
        var participantId = RequiredGuidClaim("participant_id");
        EnsureStudentScope(sessionId, participantId);
        return Data(await quiz.StartOrGetAttemptAsync(sessionId, participantId, ct));
    }

    [HttpPut("attempts/{attemptId:guid}/answers")]
    public async Task<ActionResult<ApiResponse<SyncQuizAnswersResultDto>>> Sync(Guid attemptId, SyncQuizAnswersRequest request, CancellationToken ct) =>
        Data(await quiz.SyncAnswersAsync(attemptId, RequiredGuidClaim("participant_id"), request, ct));

    [HttpPost("attempts/{attemptId:guid}/finalize")]
    public async Task<ActionResult<ApiResponse<QuizAttemptDto>>> Finalize(Guid attemptId, FinalizeQuizAttemptRequest request, CancellationToken ct) =>
        Data(await quiz.FinalizeAsync(attemptId, RequiredGuidClaim("participant_id"), request, ct));
}
