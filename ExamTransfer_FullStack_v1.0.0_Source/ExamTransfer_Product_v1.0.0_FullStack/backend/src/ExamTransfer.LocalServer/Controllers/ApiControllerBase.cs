using ExamTransfer.Application;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace ExamTransfer.LocalServer.Controllers;

[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    protected ActionResult<ApiResponse<T>> Data<T>(T value) => Ok(ApiResponse<T>.Ok(value, HttpContext.TraceIdentifier));
    protected ActionResult<ApiResponse<object>> EmptyData() => Ok(ApiResponse<object>.Ok(new { }, HttpContext.TraceIdentifier));

    protected bool IsStudent => User.IsInRole(UserRole.Student.ToString());
    protected bool IsExamParticipant => User.HasClaim(x => x.Type == "participant_id");

    protected Guid RequiredGuidClaim(string claimType)
    {
        if (Guid.TryParse(User.FindFirst(claimType)?.Value, out var id)) return id;
        throw new ApiException(ErrorCodes.Unauthorized, $"Token thiếu claim {claimType}.", 401);
    }

    protected void EnsureStudentScope(Guid sessionId, Guid participantId)
    {
        if (!IsStudent) return;
        if (!IsExamParticipant)
            throw new ApiException(ErrorCodes.ParticipantTokenRequired, "Endpoint này cần X-Exam-Session-Token.", 401);
        if (RequiredGuidClaim("session_id") != sessionId || RequiredGuidClaim("participant_id") != participantId)
            throw new ApiException(ErrorCodes.Forbidden, "Không được truy cập dữ liệu của người tham gia khác.", 403);
    }
}
