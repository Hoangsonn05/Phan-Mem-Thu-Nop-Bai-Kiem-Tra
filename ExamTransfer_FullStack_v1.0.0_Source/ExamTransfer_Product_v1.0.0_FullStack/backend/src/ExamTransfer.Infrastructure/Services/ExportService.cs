using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.Infrastructure.Services;

public sealed class ExportService(AppDbContext db, IStoragePaths paths, IAuditService audit) : IExportService
{
    public async Task<ExportJobDto> CreateAsync(CreateExportRequest request, CancellationToken cancellationToken)
    {
        _ = await db.ExamSessionsSet.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.SessionId, cancellationToken) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy phòng thi.", 404);
        var entity = new ExportJob { SessionId = request.SessionId, OptionsJson = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Status = ExportStatus.Queued, Progress = 0 };
        db.ExportJobsSet.Add(entity); await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("ExportQueued", nameof(ExportJob), entity.Id.ToString(), request.SessionId, null, request, cancellationToken);
        return ToDto(entity);
    }
    public async Task<ExportJobDto> GetAsync(Guid id, CancellationToken cancellationToken) => ToDto(await db.ExportJobsSet.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy export job.", 404));
    public async Task CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await db.ExportJobsSet.FirstOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy export job.", 404);
        if (entity.Status is ExportStatus.Completed or ExportStatus.Failed) throw new ApiException(ErrorCodes.Conflict, "Job đã kết thúc.", 409);
        entity.Status = ExportStatus.Cancelled; await db.SaveChangesAsync(cancellationToken);
    }
    public async Task<(string Path, string FileName)> GetDownloadAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await db.ExportJobsSet.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy export job.", 404);
        if (entity.Status != ExportStatus.Completed || string.IsNullOrWhiteSpace(entity.OutputPath)) throw new ApiException(ErrorCodes.Conflict, "Export chưa hoàn tất.", 409);
        var full = Path.GetFullPath(Path.Combine(paths.RootPath, entity.OutputPath)); if (!File.Exists(full)) throw new ApiException(ErrorCodes.NotFound, "File export không tồn tại.", 404);
        return (full, Path.GetFileName(full));
    }
    public static ExportJobDto ToDto(ExportJob x) => new(x.Id, x.SessionId, x.Status, x.Progress, x.OutputPath is null ? null : Path.GetFileName(x.OutputPath), x.Error, x.CreatedAtUtc, x.CompletedAtUtc);
}
