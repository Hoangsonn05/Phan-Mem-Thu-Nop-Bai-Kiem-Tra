using ExamTransfer.Application;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.LocalServer.Auth;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.LocalServer.Controllers;

[Route("api/v1/exams")]
[Authorize(AuthenticationSchemes = ExamTransferAuthSchemes.Account + "," + ExamTransferAuthSchemes.ExamParticipant)]
public sealed class ExamsController(IExamService service, AppDbContext db) : ApiControllerBase
{
    [HttpGet][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<PagedResult<ExamSummaryDto>>>> List([FromQuery] string? search, [FromQuery] ExamStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) => Data(await service.ListAsync(search, status, page, pageSize, ct));
    [HttpPost][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<ExamDetailDto>>> Create(CreateExamRequest request, CancellationToken ct) => Data(await service.CreateAsync(request, ct));
    [HttpGet("{id:guid}")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<ExamDetailDto>>> Get(Guid id, CancellationToken ct) => Data(await service.GetAsync(id, ct));
    [HttpPut("{id:guid}")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<ExamDetailDto>>> Update(Guid id, UpdateExamRequest request, CancellationToken ct) => Data(await service.UpdateAsync(id, request, ct));
    [HttpPost("{id:guid}/publish")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<ExamDetailDto>>> Publish(Guid id, CancellationToken ct) => Data(await service.PublishAsync(id, ct));
    [HttpPost("{id:guid}/archive")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<object>>> Archive(Guid id, CancellationToken ct) { await service.ArchiveAsync(id, ct); return EmptyData(); }
    [HttpPost("{id:guid}/clone")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<ExamDetailDto>>> Clone(Guid id, CancellationToken ct) => Data(await service.CloneAsync(id, ct));
    [HttpPost("{id:guid}/files/init")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<InitFileUploadResponse>>> InitFile(Guid id, InitFileUploadRequest request, CancellationToken ct) => Data(await service.InitFileAsync(id, request, ct));
    [HttpPut("{id:guid}/files/{fileId:guid}/chunks/{index:int}")][Authorize(Policy = "TeacherOrAdmin")]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<object>>> Chunk(Guid id, Guid fileId, int index, CancellationToken ct)
    {
        await service.UploadChunkAsync(id, fileId, index, Request.Body, Request.ContentLength ?? -1, Request.Headers["X-Chunk-Sha256"].FirstOrDefault(), ct);
        return EmptyData();
    }
    [HttpPost("{id:guid}/files/{fileId:guid}/finalize")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<FileDescriptorDto>>> Finalize(Guid id, Guid fileId, FinalizeFileUploadRequest request, CancellationToken ct) => Data(await service.FinalizeFileAsync(id, fileId, request, ct));
    [HttpDelete("{id:guid}/files/{fileId:guid}")][Authorize(Policy = "TeacherOrAdmin")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteFile(Guid id, Guid fileId, CancellationToken ct) { await service.DeleteFileAsync(id, fileId, ct); return EmptyData(); }

    [HttpGet("{id:guid}/manifest")]
    public async Task<ActionResult<ApiResponse<ExamManifestDto>>> Manifest(Guid id, CancellationToken ct)
    {
        await EnsureExamAccessAsync(id, ct);
        return Data(await service.GetManifestAsync(id, ct));
    }

    [HttpGet("{id:guid}/files/{fileId:guid}/content")]
    public async Task<IActionResult> Content(Guid id, Guid fileId, CancellationToken ct)
    {
        await EnsureExamAccessAsync(id, ct);
        var file = await service.GetFileContentAsync(id, fileId, ct);
        return PhysicalFile(file.Path, file.MimeType, file.DownloadName, enableRangeProcessing: true);
    }

    private async Task EnsureExamAccessAsync(Guid examId, CancellationToken ct)
    {
        if (!IsStudent) return;
        if (!IsExamParticipant)
            throw new ApiException(ErrorCodes.ParticipantTokenRequired, "Endpoint này cần X-Exam-Session-Token.", 401);
        var sessionId = RequiredGuidClaim("session_id");
        var participantId = RequiredGuidClaim("participant_id");
        var allowed = await db.SessionParticipantsSet.AsNoTracking()
            .AnyAsync(x => x.Id == participantId && x.SessionId == sessionId && x.Status == ParticipantStatus.Approved && x.Session.ExamId == examId, ct);
        if (!allowed) throw new ApiException(ErrorCodes.Forbidden, "Không được tải đề của kỳ thi này.", 403);
    }
}
