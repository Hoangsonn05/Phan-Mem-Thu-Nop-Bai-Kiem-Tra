using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ExamTransfer.Infrastructure.Security;

public sealed class AccountSessionService(AppDbContext db, IOptions<ExamTransferOptions> options) : IAccountSessionService
{
    private readonly ExamTransferOptions appOptions = options.Value;

    public async Task<UserLoginSession> ClaimAsync(User user, string deviceId, string machineName, string? ipAddress, string? encryptedRefreshToken, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var leaseExpires = now.AddSeconds(Math.Max(30, appOptions.Auth.LeaseSeconds));

        await using var transaction = await db.BeginTransactionAsync(cancellationToken);
        var sessions = await db.UserLoginSessionsSet
            .Where(x => x.UserId == user.Id && x.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);
        sessions = sessions
            .OrderByDescending(x => x.LastSeenAtUtc)
            .ToList();

        foreach (var expired in sessions.Where(x => x.ExpiresAtUtc <= now))
        {
            expired.RevokedAtUtc = now;
            expired.RevokeReason = "lease_expired";
        }

        var active = sessions.FirstOrDefault(x => x.RevokedAtUtc is null && x.ExpiresAtUtc > now);
        if (active is not null && !string.Equals(active.DeviceId, deviceId, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new ApiException(
                ErrorCodes.AccountAlreadyActive,
                "Tài khoản này đang có phiên đăng nhập hoạt động trên thiết bị khác.",
                409);
        }

        var session = active;
        if (session is null)
        {
            session = new UserLoginSession
            {
                UserId = user.Id,
                OrganizationId = user.OrganizationId ?? appOptions.Cloud.OrganizationId,
                DeviceId = deviceId,
                MachineName = machineName,
                IpAddress = ipAddress,
                LastSeenAtUtc = now,
                ExpiresAtUtc = leaseExpires,
                SessionTokenHash = "pending:" + Guid.NewGuid().ToString("N"),
                EncryptedRefreshToken = encryptedRefreshToken
            };
            db.UserLoginSessionsSet.Add(session);
        }
        else
        {
            session.MachineName = machineName;
            session.IpAddress = ipAddress;
            session.LastSeenAtUtc = now;
            session.ExpiresAtUtc = leaseExpires;
            session.EncryptedRefreshToken = encryptedRefreshToken ?? session.EncryptedRefreshToken;
        }

        user.LastLoginAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return session;
    }

    public async Task StoreTokenHashAsync(Guid loginSessionId, string tokenHash, CancellationToken cancellationToken)
    {
        var session = await db.UserLoginSessionsSet.FirstOrDefaultAsync(x => x.Id == loginSessionId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.LoginSessionExpired, "Phiên đăng nhập không còn tồn tại.", 401);
        session.SessionTokenHash = tokenHash;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AccountSessionValidation?> ValidateAsync(AccountTokenPrincipal principal, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var session = await db.UserLoginSessionsSet
            .Include(x => x.User)
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == principal.LoginSessionId && x.UserId == principal.UserId, cancellationToken);
        if (session is null || session.RevokedAtUtc is not null || session.ExpiresAtUtc <= now)
            return null;
        if (!string.Equals(session.DeviceId, principal.DeviceId, StringComparison.Ordinal))
            return null;
        if (!session.User.IsActive)
            return null;

        return new AccountSessionValidation(session.User, session);
    }

    public async Task<AccountHeartbeatResponse> HeartbeatAsync(AccountTokenPrincipal principal, string machineName, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var session = await db.UserLoginSessionsSet.FirstOrDefaultAsync(x => x.Id == principal.LoginSessionId && x.UserId == principal.UserId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.LoginSessionExpired, "Phiên đăng nhập đã hết hạn.", 401);
        if (session.RevokedAtUtc is not null || session.ExpiresAtUtc <= now)
        {
            throw new ApiException(ErrorCodes.LoginSessionExpired, "Phiên đăng nhập đã hết hạn.", 401);
        }

        if (!string.Equals(session.DeviceId, principal.DeviceId, StringComparison.Ordinal))
        {
            throw new ApiException(ErrorCodes.DeviceMismatch, "Phiên đăng nhập không thuộc thiết bị này.", 401);
        }

        session.MachineName = string.IsNullOrWhiteSpace(machineName) ? session.MachineName : machineName;
        session.LastSeenAtUtc = now;
        session.ExpiresAtUtc = now.AddSeconds(Math.Max(30, appOptions.Auth.LeaseSeconds));
        await db.SaveChangesAsync(cancellationToken);

        return new AccountHeartbeatResponse(
            true,
            now,
            session.ExpiresAtUtc,
            Math.Max(10, appOptions.Auth.HeartbeatSeconds));
    }

    public async Task LogoutAsync(AccountTokenPrincipal principal, string? reason, CancellationToken cancellationToken)
    {
        var session = await db.UserLoginSessionsSet.FirstOrDefaultAsync(x => x.Id == principal.LoginSessionId && x.UserId == principal.UserId, cancellationToken);
        if (session is null)
            return;

        if (session.RevokedAtUtc is null)
        {
            session.RevokedAtUtc = DateTimeOffset.UtcNow;
            session.RevokeReason = string.IsNullOrWhiteSpace(reason) ? "logout" : reason.Trim();
            session.EncryptedRefreshToken = null;
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
