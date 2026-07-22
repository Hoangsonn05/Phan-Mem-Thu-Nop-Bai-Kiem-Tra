using ExamTransfer.Application;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.LocalServer.Controllers;

[Route("api/v1/student")]
[Authorize(Policy = "StudentWithParticipant")]
public sealed class StudentResultsController(IGradeService grades, ISubmissionService submissions, AppDbContext db, IStoragePaths paths) : ApiControllerBase
{
    [HttpGet("submissions/{submissionId:guid}/grade")]
    public async Task<ActionResult<ApiResponse<GradeDto>>> Grade(Guid submissionId, CancellationToken ct)
    {
        var submission = await submissions.GetAsync(submissionId, ct);
        EnsureStudentScope(submission.SessionId, submission.ParticipantId);
        var grade = await grades.GetAsync(submissionId, ct);
        if (grade.Status != GradingStatus.Returned)
            throw new ApiException(ErrorCodes.Forbidden, "Kết quả chưa được giáo viên công bố.", 403);
        grade = grade with
        {
            Attachments = grade.Attachments.Select(x => x with
            {
                DownloadUrl = $"/api/v1/student/submissions/{submissionId}/grade/attachments/{x.Id}/content"
            }).ToList()
        };
        return Data(grade);
    }

    [HttpGet("submissions/{submissionId:guid}/grade/attachments/{attachmentId:guid}/content")]
    public async Task<IActionResult> Attachment(Guid submissionId, Guid attachmentId, CancellationToken ct)
    {
        var submission = await submissions.GetAsync(submissionId, ct);
        EnsureStudentScope(submission.SessionId, submission.ParticipantId);
        var attachment = await db.GradedAttachmentsSet.AsNoTracking()
            .Include(x => x.Grade)
            .FirstOrDefaultAsync(x => x.Id == attachmentId && x.Grade.SubmissionId == submissionId && x.Grade.Status == GradingStatus.Returned, ct)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy file kết quả đã công bố.", 404);
        var fullPath = Path.GetFullPath(Path.Combine(paths.RootPath, attachment.RelativePath));
        if (!fullPath.StartsWith(Path.GetFullPath(paths.RootPath), StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(fullPath))
            throw new ApiException(ErrorCodes.NotFound, "File kết quả không tồn tại.", 404);
        return PhysicalFile(fullPath, attachment.MimeType, attachment.OriginalName, true);
    }
}
