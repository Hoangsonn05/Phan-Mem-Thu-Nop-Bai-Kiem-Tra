using ExamTransfer.Application;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamTransfer.LocalServer.Controllers;

[Route("api/v1/classes")]
[Authorize(Policy = "TeacherOrAdmin")]
public sealed class ClassesController(IClassService service) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResult<ClassSummaryDto>>>> List([FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) => Data(await service.ListAsync(search, page, pageSize, ct));
    [HttpPost]
    public async Task<ActionResult<ApiResponse<ClassDetailDto>>> Create(CreateClassRequest request, CancellationToken ct) => Data(await service.CreateAsync(request, ct));
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<ClassDetailDto>>> Get(Guid id, CancellationToken ct) => Data(await service.GetAsync(id, ct));
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ApiResponse<ClassDetailDto>>> Update(Guid id, UpdateClassRequest request, CancellationToken ct) => Data(await service.UpdateAsync(id, request, ct));
    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> Archive(Guid id, CancellationToken ct) { await service.ArchiveAsync(id, ct); return EmptyData(); }
    [HttpPost("{id:guid}/students")]
    public async Task<ActionResult<ApiResponse<StudentDto>>> AddStudent(Guid id, CreateStudentRequest request, CancellationToken ct) => Data(await service.AddStudentAsync(id, request, ct));
    [HttpPut("{id:guid}/students/{studentId:guid}")]
    public async Task<ActionResult<ApiResponse<StudentDto>>> UpdateStudent(Guid id, Guid studentId, UpdateStudentRequest request, CancellationToken ct) => Data(await service.UpdateStudentAsync(id, studentId, request, ct));
    [HttpDelete("{id:guid}/students/{studentId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> RemoveStudent(Guid id, Guid studentId, CancellationToken ct) { await service.RemoveStudentAsync(id, studentId, ct); return EmptyData(); }
    [HttpPost("{id:guid}/imports/preview")]
    public async Task<ActionResult<ApiResponse<ImportPreviewDto>>> PreviewImport(Guid id, ImportPreviewRequest request, CancellationToken ct) => Data(await service.PreviewImportAsync(id, request, ct));
    [HttpPost("{id:guid}/imports/commit")]
    public async Task<ActionResult<ApiResponse<ImportCommitResultDto>>> CommitImport(Guid id, ImportCommitRequest request, CancellationToken ct) => Data(await service.CommitImportAsync(id, request, ct));
    [HttpGet("{id:guid}/export")]
    public async Task<IActionResult> Export(Guid id, CancellationToken ct) => File(await service.ExportCsvAsync(id, ct), "text/csv; charset=utf-8", $"class-{id:N}.csv");
}
