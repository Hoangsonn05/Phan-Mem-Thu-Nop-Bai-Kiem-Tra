using ExamTransfer.Application;
using ExamTransfer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.LocalServer.Controllers;

[ApiController]
[Route("api/public-cloud")]
[Authorize(Policy = "TeacherOrAdmin")]
public sealed class PublicCloudProjectionController(
    AppDbContext db,
    IPublicCloudPullWorker pullWorker) : ControllerBase
{
    [HttpGet("snapshot/{entityName}")]
    public async Task<IActionResult> Snapshot(
        string entityName,
        [FromQuery] long afterCloudVersion = 0,
        [FromQuery] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var normalized = CloudEntityOwnershipRegistry.Normalize(entityName);
        if (CloudEntityOwnershipRegistry.GetAuthority(normalized)
            is not (CloudEntityAuthority.CloudOwned or CloudEntityAuthority.SourceModeDependent))
            return BadRequest(new { code = "PUBLIC_CLOUD_ENTITY_INVALID" });

        var rows = await db.PublicCloudReplicaRecordsSet
            .AsNoTracking()
            .Where(x => x.EntityName == normalized && x.CloudVersion > afterCloudVersion)
            .OrderBy(x => x.CloudVersion)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(x => new
            {
                x.CloudEntityId,
                x.CloudVersion,
                x.CloudUpdatedAtUtc,
                x.PayloadJson
            })
            .ToListAsync(cancellationToken);
        var cursor = await db.PublicCloudPullCursorsSet.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EntityName == normalized, cancellationToken);
        return Ok(new
        {
            entityName = normalized,
            lastCloudVersion = cursor?.LastCloudVersion ?? 0,
            rows
        });
    }

    [HttpPost("pull")]
    public async Task<IActionResult> Pull(CancellationToken cancellationToken)
    {
        await pullWorker.PullOnceAsync(cancellationToken);
        return Accepted(new { status = "completed" });
    }
}
