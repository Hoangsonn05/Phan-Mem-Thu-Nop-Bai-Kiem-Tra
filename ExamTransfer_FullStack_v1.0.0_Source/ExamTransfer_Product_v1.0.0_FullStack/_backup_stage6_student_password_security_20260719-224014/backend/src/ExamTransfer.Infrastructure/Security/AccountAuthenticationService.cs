using System.Globalization;
using System.Text;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ExamTransfer.Infrastructure.Security;

public sealed class AccountAuthenticationService(
    AppDbContext db,
    IExternalIdentityProvider identityProvider,
    IAccountSessionService sessions,
    IAccountTokenService accountTokens,
    ILoginChallengeService challenges,
    IDataProtectionProvider dataProtection,
    IOptions<ExamTransferOptions> options) : IAccountAuthenticationService
{
    private readonly ExamTransferOptions appOptions = options.Value;
    private readonly IDataProtector refreshProtector = dataProtection.CreateProtector("ExamTransfer.Auth.RefreshToken.v1");

    public async Task<AccountLoginResultDto> LoginAsync(AccountLoginRequest request, string? ipAddress, CancellationToken cancellationToken)
    {
        ValidateLoginRequest(request);

        var external = await identityProvider.AuthenticateAsync(request, cancellationToken);
        var user = await ProvisionUserAsync(external, request.Account, cancellationToken);
        EnsureAccountUsable(user);

        if (user.Role == UserRole.Student)
        {
            if (string.IsNullOrWhiteSpace(user.StudentCode)
                || string.IsNullOrWhiteSpace(user.DisplayName)
                || user.DateOfBirth is null)
            {
                throw new ApiException(
                    ErrorCodes.StudentIdentityMismatch,
                    "Hồ sơ sinh viên chưa có đủ mã sinh viên, họ tên và ngày sinh.",
                    422);
            }

            if (!request.Account.Contains('@')
                && !string.Equals(
                    NormalizeCode(request.Account),
                    NormalizeCode(user.StudentCode),
                    StringComparison.Ordinal))
            {
                throw new ApiException(
                    ErrorCodes.StudentIdentityMismatch,
                    "Mã sinh viên không khớp với hồ sơ tài khoản.",
                    422);
            }
        }

        var encryptedRefreshToken = string.IsNullOrWhiteSpace(external.RefreshToken)
            ? null
            : refreshProtector.Protect(external.RefreshToken);
        return await SignInAsync(user, request.DeviceId, request.MachineName, ipAddress, encryptedRefreshToken, cancellationToken);
    }

    public async Task<AccountLoginResultDto> ConfirmStudentAsync(StudentIdentityConfirmRequest request, string? ipAddress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ChallengeToken)
            || string.IsNullOrWhiteSpace(request.DeviceId)
            || string.IsNullOrWhiteSpace(request.StudentCode)
            || string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new ApiException(ErrorCodes.ValidationFailed, "Challenge, mã sinh viên, họ tên và thiết bị là bắt buộc.");
        }

        var challenge = challenges.ValidateAndConsume(request.ChallengeToken, request.DeviceId.Trim());
        if (challenge is null)
        {
            throw new ApiException(
                ErrorCodes.StudentIdentityConfirmationRequired,
                "Challenge xác nhận sinh viên không hợp lệ hoặc đã hết hạn.",
                401);
        }

        var user = await db.UsersSet.FirstOrDefaultAsync(x => x.Id == challenge.UserId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.InvalidCredentials, "Tài khoản không hợp lệ.", 401);
        EnsureAccountUsable(user);
        if (user.Role != UserRole.Student)
            throw new ApiException(ErrorCodes.Forbidden, "Chỉ tài khoản sinh viên cần xác nhận danh tính.", 403);

        if (!string.Equals(NormalizeCode(user.StudentCode), NormalizeCode(request.StudentCode), StringComparison.Ordinal)
            || !string.Equals(NormalizeName(user.DisplayName), NormalizeName(request.DisplayName), StringComparison.Ordinal))
        {
            throw new ApiException(
                ErrorCodes.StudentIdentityMismatch,
                "Mã sinh viên hoặc họ tên không khớp với tài khoản đã được cấp.",
                422);
        }

        return await SignInAsync(user, request.DeviceId, request.MachineName, ipAddress, null, cancellationToken);
    }

    public async Task<CurrentAccountDto> GetCurrentAsync(AccountTokenPrincipal principal, CancellationToken cancellationToken)
    {
        var validation = await sessions.ValidateAsync(principal, cancellationToken)
            ?? throw new ApiException(ErrorCodes.LoginSessionExpired, "Phiên đăng nhập không hợp lệ hoặc đã hết hạn.", 401);
        return ToCurrent(validation.User, validation.Session, principal.ExpiresAtUtc);
    }

    public Task<AccountHeartbeatResponse> HeartbeatAsync(AccountTokenPrincipal principal, AccountHeartbeatRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(principal.DeviceId, request.DeviceId, StringComparison.Ordinal))
            throw new ApiException(ErrorCodes.DeviceMismatch, "Device ID không khớp với phiên đăng nhập.", 401);
        return sessions.HeartbeatAsync(principal, request.MachineName, cancellationToken);
    }

    public async Task LogoutAsync(AccountTokenPrincipal principal, LogoutRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.DeviceId)
            && !string.Equals(principal.DeviceId, request.DeviceId, StringComparison.Ordinal))
        {
            return;
        }

        await sessions.LogoutAsync(principal, request.Reason, cancellationToken);
    }

    private async Task<AccountLoginResultDto> SignInAsync(
        User user,
        string deviceId,
        string machineName,
        string? ipAddress,
        string? encryptedRefreshToken,
        CancellationToken cancellationToken)
    {
        var session = await sessions.ClaimAsync(
            user,
            deviceId.Trim(),
            machineName.Trim(),
            ipAddress,
            encryptedRefreshToken,
            cancellationToken);
        var issued = accountTokens.IssueAccountToken(
            user.Id,
            session.Id,
            user.Role,
            OrganizationId(user),
            deviceId.Trim(),
            TimeSpan.FromMinutes(Math.Clamp(appOptions.Auth.AccountTokenMinutes, 5, 24 * 60)));
        await sessions.StoreTokenHashAsync(session.Id, accountTokens.HashToken(issued.Token), cancellationToken);

        return new AccountLoginResultDto(
            true,
            false,
            null,
            user.Id,
            user.DisplayName,
            user.StudentCode,
            user.Role,
            OrganizationId(user),
            issued.Token,
            issued.ExpiresAtUtc,
            deviceId.Trim());
    }

    private async Task<User> ProvisionUserAsync(
        ExternalIdentityResult external,
        string suppliedAccount,
        CancellationToken cancellationToken)
    {
        var profile = external.Profile;
        if (!string.Equals(profile.ProviderUserId, external.ProviderUserId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(
                ErrorCodes.ProfileResponseInvalid,
                "Hồ sơ ứng dụng không thuộc tài khoản Supabase vừa đăng nhập.",
                503);
        }

        if (!Guid.TryParse(appOptions.Cloud.OrganizationId, out var configuredOrganizationId))
        {
            throw new ApiException(
                ErrorCodes.SupabaseNotConfigured,
                "Organization ID trong cấu hình phải là UUID hợp lệ.",
                503);
        }

        if (string.IsNullOrWhiteSpace(profile.OrganizationId))
        {
            throw new ApiException(
                ErrorCodes.ProfileOrganizationMissing,
                "Hồ sơ ứng dụng chưa được gán tổ chức.",
                403);
        }

        if (!Guid.TryParse(profile.OrganizationId, out var profileOrganizationId))
        {
            throw new ApiException(
                ErrorCodes.ProfileResponseInvalid,
                "Organization ID trong hồ sơ ứng dụng không hợp lệ.",
                503);
        }

        if (profileOrganizationId != configuredOrganizationId)
        {
            throw new ApiException(
                ErrorCodes.ProfileOrganizationMismatch,
                "Tài khoản không thuộc tổ chức được cấu hình cho ExamTransfer.",
                403);
        }

        if (!Enum.TryParse<UserRole>(profile.Role, true, out var role)
            || !Enum.IsDefined(role)
            || role is not (UserRole.Admin or UserRole.Teacher or UserRole.Student))
        {
            throw new ApiException(
                ErrorCodes.ProfileRoleInvalid,
                "Vai trò trong hồ sơ ứng dụng không hợp lệ.",
                403);
        }

        if (!profile.IsActive)
            throw new ApiException(ErrorCodes.AccountInactive, "Tài khoản đã bị vô hiệu hóa.", 403);

        var normalizedAccount = NormalizeCode(suppliedAccount)!;
        var normalizedExternalEmail = NormalizeCode(external.Email);
        var normalizedProfileUsername = NormalizeCode(profile.Username);
        var user = await db.UsersSet
            .FirstOrDefaultAsync(x => x.SupabaseAuthUserId == external.ProviderUserId, cancellationToken);

        if (user is null)
        {
            var legacyMatches = await db.UsersSet
                .Where(x => string.IsNullOrEmpty(x.SupabaseAuthUserId)
                    && (x.Username.ToLower() == normalizedAccount
                        || (normalizedProfileUsername != null && x.Username.ToLower() == normalizedProfileUsername)
                        || (x.Email != null && x.Email.ToLower() == normalizedAccount)
                        || (x.Email != null && normalizedExternalEmail != null && x.Email.ToLower() == normalizedExternalEmail)))
                .ToListAsync(cancellationToken);

            if (legacyMatches.Count > 1)
                throw ProvisioningConflict();

            user = legacyMatches.SingleOrDefault();
            if (user is null)
            {
                user = new User();
                db.UsersSet.Add(user);
            }
        }

        var username = FirstNonBlank(profile.Username, user.Username, external.Account, suppliedAccount)!;
        var displayName = FirstNonBlank(profile.DisplayName, user.DisplayName, username)!;
        var email = FirstNonBlank(external.Email, user.Email);
        var studentCode = FirstNonBlank(profile.StudentCode, user.StudentCode);
        var normalizedUsername = NormalizeCode(username)!;
        var normalizedEmail = NormalizeCode(email);
        var normalizedStudentCode = NormalizeCode(studentCode);

        var duplicateExists = await db.UsersSet.AnyAsync(x => x.Id != user.Id
            && (x.Username.ToLower() == normalizedUsername
                || (normalizedEmail != null && x.Email != null && x.Email.ToLower() == normalizedEmail)
                || (normalizedStudentCode != null && x.StudentCode != null && x.StudentCode.ToLower() == normalizedStudentCode)
                || x.SupabaseAuthUserId == external.ProviderUserId), cancellationToken);
        if (duplicateExists)
            throw ProvisioningConflict();

        user.SupabaseAuthUserId = external.ProviderUserId;
        user.OrganizationId = profileOrganizationId.ToString();
        user.DateOfBirth = profile.DateOfBirth;
        user.MustChangePassword = profile.MustChangePassword;
        user.Username = username.Trim();
        user.DisplayName = displayName.Trim();
        user.Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        user.StudentCode = string.IsNullOrWhiteSpace(studentCode) ? null : studentCode.Trim();
        user.Role = role;
        user.IsActive = true;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw ProvisioningConflict();
        }

        return user;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static ApiException ProvisioningConflict() =>
        new(
            ErrorCodes.AccountProvisioningConflict,
            "Không thể liên kết hồ sơ Supabase vì dữ liệu tài khoản cục bộ bị trùng.",
            409);

    private static void ValidateLoginRequest(AccountLoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Account)
            || string.IsNullOrWhiteSpace(request.Password)
            || string.IsNullOrWhiteSpace(request.DeviceId)
            || string.IsNullOrWhiteSpace(request.MachineName))
        {
            throw new ApiException(ErrorCodes.ValidationFailed, "Tài khoản, mật khẩu, Device ID và tên máy là bắt buộc.");
        }
    }

    private static void EnsureAccountUsable(User user)
    {
        if (!user.IsActive)
            throw new ApiException(ErrorCodes.AccountInactive, "Tài khoản đã bị vô hiệu hóa.", 403);
        if (user.Role is not (UserRole.Admin or UserRole.Teacher or UserRole.Student))
            throw new ApiException(ErrorCodes.Forbidden, "Vai trò tài khoản không được hỗ trợ.", 403);
    }

    private CurrentAccountDto ToCurrent(User user, UserLoginSession session, DateTimeOffset tokenExpiresAtUtc) =>
        new(
            user.Id,
            user.Username,
            user.Email,
            user.DisplayName,
            user.StudentCode,
            user.Role,
            OrganizationId(user),
            session.Id,
            session.DeviceId,
            tokenExpiresAtUtc,
            user.DateOfBirth,
            user.MustChangePassword);

    private string? OrganizationId(User user) =>
        user.OrganizationId ?? appOptions.Cloud.OrganizationId;

    private static string? NormalizeCode(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var collapsed = string.Join(' ', value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var normalized = collapsed.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
