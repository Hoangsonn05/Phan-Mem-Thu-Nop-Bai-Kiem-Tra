using ExamTransfer.Application;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamTransfer.LocalServer.Controllers;

[Route("api/v1/exams/{examId:guid}/quiz")]
[Authorize(Policy = "TeacherOrAdmin")]
public sealed class QuizAuthoringController(IQuizService quiz) : ApiControllerBase
{
    [HttpPost("import")]
    public async Task<ActionResult<ApiResponse<QuizImportResultDto>>> Import(Guid examId, QuizImportFileRequest request, CancellationToken ct) =>
        Data(await quiz.ImportAsync(examId, request, ct));
}

[Route("api/v1/student/quiz")]
[Authorize(Policy = "StudentWithParticipant")]
public sealed class StudentQuizController(IQuizService quiz) : ApiControllerBase
{
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
