using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Services;

public sealed record UnifiedLoginResult(
    CurrentAccountDto Account,
    string AccessToken,
    AuthSessionAuthority Authority);

public interface IUnifiedAuthenticationService
{
    Task<UnifiedLoginResult> LoginAsync(
        string account,
        string password,
        string deviceId,
        string machineName,
        string appVersion,
        CancellationToken cancellationToken);
}

public sealed class UnifiedAuthenticationService(
    IBackendClient backend,
    SupabasePublicCloudClient publicCloud,
    LocalServerLifecycleService localServer) : IUnifiedAuthenticationService
{
    public async Task<UnifiedLoginResult> LoginAsync(
        string account,
        string password,
        string deviceId,
        string machineName,
        string appVersion,
        CancellationToken cancellationToken)
    {
        backend.SetAccountToken(null);
        backend.SetParticipantToken(null);
        publicCloud.Logout();

        SupabaseAuthenticatedAccount cloudIdentity;
        try
        {
            cloudIdentity = await publicCloud.AuthenticateAccountAsync(
                account,
                password,
                deviceId,
                cancellationToken);
        }
        catch (Exception ex)
        {
            var step = ex is PublicCloudApiException cloud
                && cloud.Code is "PUBLICCLOUD_NOT_CONFIGURED"
                    or "PUBLICCLOUD_INVALID_URL"
                    or "PUBLICCLOUD_INVALID_PUBLISHABLE_KEY"
                    or "PUBLICCLOUD_INVALID_ORGANIZATION_ID"
                    or "PUBLICCLOUD_ENV_OVERRIDE_INCOMPLETE"
                ? "ConfigPreflight"
                : "FrontendAuth";
            LogAuthenticationFailure(step, publicCloud, ex);
            throw;
        }
        var authoritative = cloudIdentity.Account;

        if (authoritative.Role == UserRole.Student)
        {
            return new(
                authoritative,
                cloudIdentity.AccessToken,
                AuthSessionAuthority.Supabase);
        }

        if (authoritative.Role is not (UserRole.Teacher or UserRole.Admin))
            throw new InvalidOperationException(ErrorCodes.AuthenticatedRoleInvalid);

        var lifecycle = await localServer.EnsureStartedAsync(
            authoritative.Role,
            cancellationToken);
        if (lifecycle.Status is not ("SERVER_HEALTHY" or "SERVER_STARTED"))
        {
            LogAuthenticationFailure(
                "LocalServerStart",
                publicCloud,
                new InvalidOperationException(lifecycle.Status),
                lifecycle.Status);
            await localServer.StopOwnedAsync(CancellationToken.None);
            throw new InvalidOperationException(
                $"{lifecycle.Status}: {lifecycle.Message}");
        }

        var localStage = "LocalServerAuth";
        try
        {
            var login = ApiGuard.Require(await backend.PostAsync<
                AccountLoginRequest,
                AccountLoginResultDto>(
                "api/v1/auth/login",
                new AccountLoginRequest(
                    account,
                    password,
                    deviceId,
                    machineName,
                    appVersion),
                cancellationToken));
            if (login.RequiresStudentConfirmation
                || string.IsNullOrWhiteSpace(login.AccessToken))
                throw new InvalidOperationException("Local Server did not issue a valid account session.");

            backend.SetAccountToken(login.AccessToken);
            var local = ApiGuard.Require(await backend.GetAsync<CurrentAccountDto>(
                "api/v1/auth/me",
                cancellationToken));
            localStage = "LocalProfileValidation";
            EnsureSameAuthority(authoritative, local);

            // The Local Server performed its own Supabase authentication and now
            // owns the protected Teacher/Admin cloud session used by workers.
            publicCloud.Logout();
            return new(
                local,
                login.AccessToken,
                AuthSessionAuthority.LocalServer);
        }
        catch (Exception ex)
        {
            LogAuthenticationFailure(localStage, publicCloud, ex);
            backend.SetAccountToken(null);
            publicCloud.Logout();
            await localServer.StopOwnedAsync(CancellationToken.None);
            throw;
        }
    }

    private static void LogAuthenticationFailure(
        string step,
        SupabasePublicCloudClient publicCloud,
        Exception exception,
        string? explicitErrorCode = null)
    {
        var options = publicCloud.RuntimeOptions;
        var status = exception is HttpRequestException http
            ? http.StatusCode.HasValue
                ? ((int)http.StatusCode.Value).ToString()
                : "network"
            : "n/a";
        var code = explicitErrorCode ?? exception switch
        {
            PublicCloudApiException cloud => cloud.Code,
            HttpRequestException => ErrorCodes.AuthProviderUnavailable,
            _ => exception.GetType().Name
        };
        FrontendLogger.LogWarning(
            $"step={step}; configSource={options.Source}; host={options.ProjectUri?.Host ?? "<missing>"}; " +
            $"organizationId={options.OrganizationId?.ToString("D") ?? "<missing>"}; " +
            $"status={status}; errorCode={code}",
            "UnifiedAuthentication");
    }

    private static void EnsureSameAuthority(
        CurrentAccountDto cloud,
        CurrentAccountDto local)
    {
        if (local.Role != cloud.Role
            || local.Role is not (UserRole.Teacher or UserRole.Admin)
            || string.IsNullOrWhiteSpace(local.ProviderUserId)
            || !string.Equals(
                local.ProviderUserId,
                cloud.ProviderUserId,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                local.OrganizationId,
                cloud.OrganizationId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "AUTHENTICATED_PROFILE_MISMATCH: Local Server profile does not match the authenticated Supabase account.");
        }
    }
}
