using ExamTransfer.Application;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamTransfer.LocalServer.Controllers;

[Route("api/v1/exports")]
[Authorize(Policy = "TeacherOrAdmin")]
public sealed class ExportsController(IExportService service) : ApiControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ApiResponse<ExportJobDto>>> Create(CreateExportRequest request, CancellationToken ct) => Data(await service.CreateAsync(request, ct));
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<ExportJobDto>>> Get(Guid id, CancellationToken ct) => Data(await service.GetAsync(id, ct));
    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult<ApiResponse<object>>> Cancel(Guid id, CancellationToken ct) { await service.CancelAsync(id, ct); return EmptyData(); }
    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct) { var f = await service.GetDownloadAsync(id, ct); return PhysicalFile(f.Path, "application/zip", f.FileName, true); }
}
