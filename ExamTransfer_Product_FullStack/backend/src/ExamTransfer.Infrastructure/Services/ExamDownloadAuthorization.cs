using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.Infrastructure.Services;

internal sealed record ExamDownloadAuthorizationDecision(
    bool Authorized,
    string? OrganizationId,
    string Branch);

internal static class ExamDownloadAuthorization
{
    public static async Task<ExamDownloadAuthorizationDecision> AuthorizeAsync(
        AppDbContext db,
        User actor,
        string? actorOrganizationId,
        Guid examId,
        Guid? createdBy,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actorOrganizationId)
            || string.IsNullOrWhiteSpace(actor.OrganizationId)
            || !string.Equals(actor.OrganizationId, actorOrganizationId, StringComparison.Ordinal))
        {
            return new(false, null, "ActorOrganizationMismatch");
        }

        var (owner, legacyAudit) = await ResolveOwnerAsync(
            db,
            examId,
            createdBy,
            cancellationToken);
        if (owner is null)
            return new(false, null, "ExamOwnerUnresolved");

        if (string.IsNullOrWhiteSpace(owner.OrganizationId)
            || !string.Equals(owner.OrganizationId, actorOrganizationId, StringComparison.Ordinal))
        {
            return new(false, null, "CrossOrganization");
        }

        var branch = legacyAudit
            ? actor.Id == owner.Id ? "LegacyAuditOwner" : "LegacyAuditOrganization"
            : actor.Id == owner.Id ? "CreatedByOwner" : "CreatedByOrganization";
        return new(true, actorOrganizationId, branch);
    }

    private static async Task<(User? Owner, bool LegacyAudit)> ResolveOwnerAsync(
        AppDbContext db,
        Guid examId,
        Guid? createdBy,
        CancellationToken cancellationToken)
    {
        if (createdBy.HasValue)
        {
            var owner = await db.UsersSet.AsNoTracking()
                .SingleOrDefaultAsync(
                    user => user.Id == createdBy.Value
                        && user.IsActive
                        && (user.Role == UserRole.Teacher || user.Role == UserRole.Admin),
                    cancellationToken);
            return (owner, false);
        }

        var auditRows = await db.AuditLogsSet.AsNoTracking()
            .Where(audit => audit.Action == "ExamCreated"
                && audit.EntityType == nameof(Exam)
                && audit.EntityId == examId.ToString())
            .Select(audit => new
            {
                audit.Id,
                audit.ActorId,
                audit.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);
        if (auditRows.Count == 0)
            return (null, true);

        var parsedActorIds = auditRows
            .Select(audit => Guid.TryParse(audit.ActorId, out var actorId)
                ? actorId
                : (Guid?)null)
            .Where(actorId => actorId.HasValue)
            .Select(actorId => actorId!.Value)
            .Distinct()
            .ToArray();
        if (parsedActorIds.Length == 0)
            return (null, true);

        var validActors = await db.UsersSet.AsNoTracking()
            .Where(user => parsedActorIds.Contains(user.Id)
                && user.IsActive
                && (user.Role == UserRole.Teacher || user.Role == UserRole.Admin)
                && user.OrganizationId != null
                && user.OrganizationId != string.Empty)
            .ToDictionaryAsync(user => user.Id, cancellationToken);

        foreach (var timestampGroup in auditRows
            .OrderBy(audit => audit.CreatedAtUtc)
            .ThenBy(audit => audit.Id)
            .GroupBy(audit => audit.CreatedAtUtc))
        {
            var owners = timestampGroup
                .Select(audit => Guid.TryParse(audit.ActorId, out var actorId)
                    && validActors.TryGetValue(actorId, out var actor)
                        ? actor
                        : null)
                .Where(actor => actor is not null)
                .Cast<User>()
                .GroupBy(actor => actor.Id)
                .Select(group => group.First())
                .ToArray();
            if (owners.Length == 0)
                continue;
            if (owners.Length > 1)
                return (null, true);
            return (owners[0], true);
        }

        return (null, true);
    }
}
