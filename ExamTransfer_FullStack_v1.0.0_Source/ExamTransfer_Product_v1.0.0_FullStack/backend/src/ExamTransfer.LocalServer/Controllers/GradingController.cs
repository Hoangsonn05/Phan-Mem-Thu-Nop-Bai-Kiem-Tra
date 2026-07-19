using ExamTransfer.Application;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.LocalServer.Controllers;

[Route("api/v1/grading")]
[Authorize(Policy = "TeacherOrAdmin")]
public sealed class GradingController(IGradeService service, AppDbContext db, IStoragePaths paths) : ApiControllerBase
{
    [HttpGet("queue")]
    public async Task<ActionResult<ApiResponse<PagedResult<SubmissionSummaryDto>>>> Queue([FromQuery] GradingStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default) => Data(await service.GetQueueAsync(status, page, pageSize, ct));
    [HttpGet("submissions/{id:guid}")]
    public async Task<ActionResult<ApiResponse<GradeDto>>> Get(Guid id, CancellationToken ct) => Data(await service.GetAsync(id, ct));
    [HttpPut("submissions/{id:guid}")]
    public async Task<ActionResult<ApiResponse<GradeDto>>> Save(Guid id, SaveGradeRequest request, CancellationToken ct) => Data(await service.SaveAsync(id, request, ct));
    [HttpPost("submissions/{id:guid}/return")]
    public async Task<ActionResult<ApiResponse<GradeDto>>> Return(Guid id, ReturnGradeRequest request, CancellationToken ct) => Data(await service.ReturnAsync(id, request, ct));
    [HttpPost("submissions/{id:guid}/reopen")]
    public async Task<ActionResult<ApiResponse<GradeDto>>> Reopen(Guid id, ReopenGradeRequest request, CancellationToken ct) => Data(await service.ReopenAsync(id, request, ct));
    [HttpPost("submissions/{id:guid}/attachments")][RequestSizeLimit(100 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<FileDescriptorDto>>> Attachment(Guid id, [FromQuery] string fileName, [FromQuery] string mimeType, CancellationToken ct) => Data(await service.AddAttachmentAsync(id, fileName, mimeType, Request.Body, Request.ContentLength ?? -1, ct));
    [HttpGet("submissions/{submissionId:guid}/attachments/{attachmentId:guid}/content")]
    public async Task<IActionResult> AttachmentContent(Guid submissionId, Guid attachmentId, CancellationToken ct)
    {
        var a = await db.GradedAttachmentsSet.AsNoTracking().Include(x => x.Grade).FirstOrDefaultAsync(x => x.Id == attachmentId && x.Grade.SubmissionId == submissionId, ct) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy file đã chấm.", 404);
        var full = Path.GetFullPath(Path.Combine(paths.RootPath, a.RelativePath)); if (!System.IO.File.Exists(full)) throw new ApiException(ErrorCodes.NotFound, "File vật lý không tồn tại.", 404); return PhysicalFile(full, a.MimeType, a.OriginalName, true);
    }
    [HttpGet("gradebook/export")]
    public async Task<IActionResult> Export([FromQuery] Guid? sessionId, CancellationToken ct) => File(await service.ExportGradebookCsvAsync(sessionId, ct), "text/csv; charset=utf-8", "gradebook.csv");
}

