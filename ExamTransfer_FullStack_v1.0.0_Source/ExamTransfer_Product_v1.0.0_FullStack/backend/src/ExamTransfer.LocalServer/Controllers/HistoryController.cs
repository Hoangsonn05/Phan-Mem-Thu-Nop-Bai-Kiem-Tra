using ExamTransfer.Application;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.LocalServer.Controllers;

[Route("api/v1")]
[Authorize(Policy = "TeacherOrAdmin")]
public sealed class HistoryController(AppDbContext db, ISessionService sessions) : ApiControllerBase
{
    [HttpGet("history/sessions")]
    public async Task<ActionResult<ApiResponse<PagedResult<SessionSummaryDto>>>> Sessions([FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) => Data(await sessions.ListAsync(null, page, pageSize, ct));
    [HttpGet("history/sessions/{id:guid}")]
    public async Task<ActionResult<ApiResponse<SessionDetailDto>>> Session(Guid id, CancellationToken ct) => Data(await sessions.GetAsync(id, ct));

    [HttpGet("audit-logs")]
    public async Task<ActionResult<ApiResponse<PagedResult<AuditLogDto>>>> Audit([FromQuery] Guid? sessionId, [FromQuery] string? action, [FromQuery] DateTimeOffset? fromUtc, [FromQuery] DateTimeOffset? toUtc, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 500);
        var query = db.AuditLogsSet.AsNoTracking().AsQueryable();
        if (sessionId.HasValue) query = query.Where(x => x.SessionId == sessionId.Value);
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(x => x.Action.Contains(action));
        if (fromUtc.HasValue) query = query.Where(x => x.CreatedAtUtc >= fromUtc.Value);
        if (toUtc.HasValue) query = query.Where(x => x.CreatedAtUtc <= toUtc.Value);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Data(new PagedResult<AuditLogDto>(rows.Select(x => new AuditLogDto(x.Id, x.SessionId, x.ActorId, x.Action, x.EntityType, x.EntityId, x.IpAddress, x.BeforeJson, x.AfterJson, x.TraceId, x.CreatedAtUtc)).ToList(), page, pageSize, total));
    }

    [HttpPost("audit-logs/export")]
    public async Task<IActionResult> ExportAudit([FromBody] Dictionary<string, string>? filters, CancellationToken ct)
    {
        var rows = await db.AuditLogsSet.AsNoTracking().OrderBy(x => x.CreatedAtUtc).Take(100000).ToListAsync(ct);
        var sb = new System.Text.StringBuilder("time,actor,action,entityType,entityId,sessionId,ip,traceId\n");
        foreach (var x in rows) sb.AppendLine($"{x.CreatedAtUtc:O},{E(x.ActorId)},{E(x.Action)},{E(x.EntityType)},{E(x.EntityId)},{x.SessionId},{E(x.IpAddress)},{E(x.TraceId)}");
        return File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv; charset=utf-8", "audit-logs.csv");
    }
    private static string E(string? value) { value ??= string.Empty; return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value; }
}
