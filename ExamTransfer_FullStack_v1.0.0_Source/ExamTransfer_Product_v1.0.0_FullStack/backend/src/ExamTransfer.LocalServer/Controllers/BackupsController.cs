using ExamTransfer.Application;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamTransfer.LocalServer.Controllers;

[Route("api/v1/backups")]
[Authorize(Policy = "TeacherOrAdmin")]
public sealed class BackupsController(IBackupService service) : ApiControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ApiResponse<BackupDto>>> Create(CreateBackupRequest request, CancellationToken ct) => Data(await service.CreateAsync(request, ct));
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<BackupDto>>>> List(CancellationToken ct) => Data(await service.ListAsync(ct));
    [HttpPost("{id:guid}/validate")]
    public async Task<ActionResult<ApiResponse<BackupDto>>> Validate(Guid id, CancellationToken ct) => Data(await service.ValidateAsync(id, ct));
    [HttpPost("{id:guid}/restore")]
    public async Task<ActionResult<ApiResponse<RestoreScheduledDto>>> Restore(Guid id, RestoreBackupRequest request, CancellationToken ct) => Data(await service.ScheduleRestoreAsync(id, request, ct));
    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var file = await service.GetDownloadAsync(id, ct);
        var contentType = string.Equals(
            Path.GetExtension(file.FileName),
            ".etb",
            StringComparison.OrdinalIgnoreCase)
            ? "application/octet-stream"
            : "application/zip";

        return PhysicalFile(file.Path, contentType, file.FileName, true);
    }
}
