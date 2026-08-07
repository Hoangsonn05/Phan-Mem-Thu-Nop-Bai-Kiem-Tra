using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class StudentPasswordGateCharacterizationTests
{
    [Fact]
    public void RestoreCachedStudentSession_RefreshesAuthoritativeAccountBeforeBuildingShell()
    {
        var source = File.ReadAllText(FindSource("ViewModels", "MainViewModel.cs"));

        Assert.DoesNotContain("current = restored.Account;", source, StringComparison.Ordinal);
        Assert.Contains("RestoreAuthenticatedAccountAsync", source, StringComparison.Ordinal);
        Assert.Contains(
            "if (authState.IsStudent && authState.MustChangePassword)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StudentPasswordChange_ReplacesTransientCredentialBeforeCompletingNavigation()
    {
        var source = File.ReadAllText(FindSource("ViewModels", "ChangePasswordViewModel.cs"));
        var setAuthenticated = source.IndexOf("authState.SetAuthenticated(", StringComparison.Ordinal);
        var setTransient = source.IndexOf("authState.SetTransientCredentials(", StringComparison.Ordinal);
        var completed = source.IndexOf("await completed();", StringComparison.Ordinal);

        Assert.True(setAuthenticated >= 0, "Password change must update the authoritative account cache.");
        Assert.True(
            setTransient > setAuthenticated,
            "Password change must replace the temporary transient credential after the account update.");
        Assert.True(
            completed > setTransient,
            "Navigation must remain gated until the transient credential uses the new password.");
    }

    [Fact]
    public async Task RestoreCachedStudentSession_AuthoritativeProfileNowRequiresPassword_UsesAuthoritativeAccount()
    {
        var path = SessionPath("restore-authoritative");
        try
        {
            var userId = Guid.NewGuid();
            var organizationId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var cached = StudentAccount(userId, organizationId, sessionId, mustChangePassword: false);
            var token = Jwt(userId, sessionId);
            var state = new AppAuthSessionState(path);
            state.SetAuthenticated(cached, token, AuthSessionAuthority.Supabase);
            Assert.True(
                new AppAuthSessionState(path)
                    .TryRestoreAuthenticatedSession(out var restored));
            Assert.False(restored.Account.MustChangePassword);

            var handler = new PasswordFlowHandler(userId, organizationId)
            {
                ProfileMustChangePassword = true
            };
            var client = PublicCloud(handler, organizationId);

            var refreshed = await client.RestoreAuthenticatedAccountAsync(
                restored.AccessToken,
                restored.Account.ProviderUserId!,
                restored.Account.ExpiresAtUtc,
                restored.Account.DeviceId,
                default);

            Assert.True(refreshed.Account.MustChangePassword);
            Assert.Equal(userId, refreshed.Account.UserId);
            Assert.Single(handler.RequestPaths, "/rest/v1/profiles");
        }
        finally
        {
            DeleteSessionDirectory(path);
        }
    }

    [Fact]
    public async Task RestoreCachedStudentSession_ProfileRefreshFailure_LogsOutCloudSession()
    {
        var userId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var handler = new PasswordFlowHandler(userId, organizationId)
        {
            ProfileStatusCode = HttpStatusCode.ServiceUnavailable
        };
        var client = PublicCloud(handler, organizationId);

        await Assert.ThrowsAsync<PublicCloudApiException>(() =>
            client.RestoreAuthenticatedAccountAsync(
                Jwt(userId, sessionId),
                userId.ToString("D"),
                DateTimeOffset.UtcNow.AddHours(1),
                "device-1",
                default));

        Assert.False(client.Authenticated);
    }

    [Fact]
    public void RestoreFailure_LoginPageShowsClearReauthenticationMessage()
    {
        var viewModel = new LoginViewModel(
            new BackendClient("http://localhost:5048", new RejectingHandler()),
            new AppAuthSessionState(SessionPath("restore-message")),
            () => Task.CompletedTask,
            new NeverAuthentication(),
            "Không thể xác minh phiên đăng nhập đã lưu. Vui lòng đăng nhập lại.");

        Assert.Contains("đăng nhập lại", viewModel.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("danger", viewModel.StatusTone);
    }

    [Fact]
    public async Task RestoreCachedStudentSession_AuthoritativeProfileClearedPasswordGate_UsesAuthoritativeFalse()
    {
        var path = SessionPath("restore-cleared");
        try
        {
            var userId = Guid.NewGuid();
            var organizationId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var cached = StudentAccount(userId, organizationId, sessionId, mustChangePassword: true);
            var token = Jwt(userId, sessionId);
            var state = new AppAuthSessionState(path);
            state.SetAuthenticated(cached, token, AuthSessionAuthority.Supabase);
            Assert.True(
                new AppAuthSessionState(path)
                    .TryRestoreAuthenticatedSession(out var restored));

            var handler = new PasswordFlowHandler(userId, organizationId)
            {
                ProfileMustChangePassword = false
            };
            var refreshed = await PublicCloud(handler, organizationId)
                .RestoreAuthenticatedAccountAsync(
                    restored.AccessToken,
                    restored.Account.ProviderUserId!,
                    restored.Account.ExpiresAtUtc,
                    restored.Account.DeviceId,
                    default);

            Assert.False(refreshed.Account.MustChangePassword);
        }
        finally
        {
            DeleteSessionDirectory(path);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DirectLogin_MapsAuthoritativePasswordGateFlag(bool mustChangePassword)
    {
        var userId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var handler = new PasswordFlowHandler(userId, organizationId)
        {
            ProfileMustChangePassword = mustChangePassword
        };

        var authenticated = await PublicCloud(handler, organizationId)
            .AuthenticateAccountAsync(
                "HS001",
                "Temporary#123",
                "device-1",
                default);

        Assert.Equal(mustChangePassword, authenticated.Account.MustChangePassword);
    }

    [Fact]
    public async Task PasswordChangeSuccess_UpdatesAccountCacheAndTransientCredentialBeforeCompletion()
    {
        var path = SessionPath("password-success");
        try
        {
            var userId = Guid.NewGuid();
            var organizationId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var current = StudentAccount(userId, organizationId, sessionId, mustChangePassword: true);
            var changed = current with { MustChangePassword = false };
            var oldToken = Jwt(userId, sessionId);
            var newToken = Jwt(userId, sessionId);
            var state = new AppAuthSessionState(path);
            state.SetAuthenticated(current, oldToken, AuthSessionAuthority.Supabase);
            state.SetTransientCredentials(current.Username, "Temporary#123");
            var completedCalls = 0;
            var viewModel = new ChangePasswordViewModel(
                new BackendClient("http://localhost:5048", new RejectingHandler()),
                state,
                () =>
                {
                    completedCalls++;
                    return Task.CompletedTask;
                },
                (_, _, _, _, _) => Task.FromResult(
                    new SupabaseAuthenticatedAccount(changed, newToken)));

            await ExecutePasswordChangeAsync(viewModel, "Temporary#123", "NewSecure#456");

            Assert.Equal(1, completedCalls);
            Assert.False(state.MustChangePassword);
            Assert.True(state.TryGetTransientCredentials(out var account, out var password));
            Assert.Equal(current.Email, account);
            Assert.Equal("NewSecure#456", password);
            Assert.True(
                new AppAuthSessionState(path)
                    .TryRestoreAuthenticatedSession(out var restored));
            Assert.False(restored.Account.MustChangePassword);
            Assert.Equal(newToken, restored.AccessToken);
        }
        finally
        {
            DeleteSessionDirectory(path);
        }
    }

    [Fact]
    public async Task PasswordChange_ProfileCompletionFailure_KeepsGateAndStopsUsingTemporaryCredential()
    {
        var path = SessionPath("password-partial");
        try
        {
            var userId = Guid.NewGuid();
            var organizationId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var current = StudentAccount(userId, organizationId, sessionId, mustChangePassword: true);
            var state = new AppAuthSessionState(path);
            state.SetAuthenticated(
                current,
                Jwt(userId, sessionId),
                AuthSessionAuthority.Supabase);
            state.SetTransientCredentials(current.Username, "Temporary#123");
            var completedCalls = 0;
            var viewModel = new ChangePasswordViewModel(
                new BackendClient("http://localhost:5048", new RejectingHandler()),
                state,
                () =>
                {
                    completedCalls++;
                    return Task.CompletedTask;
                },
                (_, _, _, _, _) => Task.FromException<SupabaseAuthenticatedAccount>(
                    new PublicCloudApiException(
                        ErrorCodes.PasswordChangeFailed,
                        "Hãy dùng mật khẩu mới cho lần thử tiếp theo.",
                        HttpStatusCode.ServiceUnavailable)));

            await ExecutePasswordChangeAsync(viewModel, "Temporary#123", "NewSecure#456");

            Assert.Equal(0, completedCalls);
            Assert.True(state.MustChangePassword);
            Assert.True(state.TryGetTransientCredentials(out _, out var password));
            Assert.Equal("NewSecure#456", password);
            Assert.Contains("mật khẩu mới", viewModel.Status, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteSessionDirectory(path);
        }
    }

    [Theory]
    [InlineData("Valid#Uneti123")]
    [InlineData("Valid#HS001123")]
    [InlineData("Valid#studentuser123")]
    public async Task DirectSupabasePasswordChange_RejectsIdentityTermsBeforeCallingSupabase(
        string password)
    {
        var userId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var handler = new PasswordFlowHandler(userId, organizationId);
        var client = PublicCloud(handler, organizationId);
        var account = StudentAccount(
            userId,
            organizationId,
            Guid.NewGuid(),
            mustChangePassword: true) with
        {
            Username = "studentuser"
        };

        var error = await Assert.ThrowsAsync<PublicCloudApiException>(() =>
            client.ChangeOwnPasswordAsync(
                account,
                "Temporary#123",
                password,
                password,
                account.DeviceId,
                default));

        Assert.Equal(ErrorCodes.PasswordPolicyRejected, error.Code);
        Assert.Empty(handler.RequestPaths);
    }

    [Theory]
    [InlineData("Short#1", "Short#1")]
    [InlineData("NewSecure#456", "Different#789")]
    [InlineData("Temporary#123", "Temporary#123")]
    public async Task DirectSupabasePasswordChange_RejectsInvalidPolicyBeforeCallingSupabase(
        string newPassword,
        string confirmPassword)
    {
        var userId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var handler = new PasswordFlowHandler(userId, organizationId);
        var client = PublicCloud(handler, organizationId);
        var account = StudentAccount(
            userId,
            organizationId,
            Guid.NewGuid(),
            mustChangePassword: true);

        var error = await Assert.ThrowsAsync<PublicCloudApiException>(() =>
            client.ChangeOwnPasswordAsync(
                account,
                "Temporary#123",
                newPassword,
                confirmPassword,
                account.DeviceId,
                default));

        Assert.Equal(ErrorCodes.PasswordPolicyRejected, error.Code);
        Assert.Empty(handler.RequestPaths);
    }

    [Fact]
    public async Task DirectSupabasePasswordChange_InvalidCurrentPassword_DoesNotUpdateAuthOrProfile()
    {
        var userId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var account = StudentAccount(
            userId,
            organizationId,
            Guid.NewGuid(),
            mustChangePassword: true);
        var handler = new PasswordFlowHandler(userId, organizationId)
        {
            TokenStatusCode = HttpStatusCode.Unauthorized
        };
        var client = PublicCloud(handler, organizationId);

        var error = await Assert.ThrowsAsync<PublicCloudApiException>(() =>
            client.ChangeOwnPasswordAsync(
                account,
                "WrongCurrent#123",
                "NewSecure#456",
                "NewSecure#456",
                account.DeviceId,
                default));

        Assert.Equal(ErrorCodes.InvalidCredentials, error.Code);
        Assert.Equal(["/auth/v1/token"], handler.RequestPaths);
        Assert.False(client.Authenticated);
    }

    [Fact]
    public async Task DirectSupabasePasswordChange_AuthUpdateFailure_DoesNotCallProfileRpc()
    {
        var userId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var account = StudentAccount(
            userId,
            organizationId,
            Guid.NewGuid(),
            mustChangePassword: true);
        var handler = new PasswordFlowHandler(userId, organizationId)
        {
            UserUpdateStatusCode = HttpStatusCode.BadRequest
        };
        var client = PublicCloud(handler, organizationId);

        await Assert.ThrowsAsync<PublicCloudApiException>(() =>
            client.ChangeOwnPasswordAsync(
                account,
                "Temporary#123",
                "NewSecure#456",
                "NewSecure#456",
                account.DeviceId,
                default));

        Assert.Equal(
            ["/auth/v1/token", "/auth/v1/user"],
            handler.RequestPaths);
        Assert.DoesNotContain(
            "/rest/v1/rpc/complete_own_password_change",
            handler.RequestPaths);
    }

    [Fact]
    public async Task DirectSupabasePasswordChange_CompletesAuthRpcAndAuthoritativeRefreshInOrder()
    {
        var userId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var account = StudentAccount(
            userId,
            organizationId,
            Guid.NewGuid(),
            mustChangePassword: true);
        var handler = new PasswordFlowHandler(userId, organizationId)
        {
            ProfileMustChangePassword = false,
            RpcCompleted = true
        };
        var client = PublicCloud(handler, organizationId);

        var result = await client.ChangeOwnPasswordAsync(
            account,
            "Temporary#123",
            "NewSecure#456",
            "NewSecure#456",
            account.DeviceId,
            default);

        Assert.False(result.Account.MustChangePassword);
        Assert.Equal(
            [
                "/auth/v1/token",
                "/auth/v1/user",
                "/rest/v1/rpc/complete_own_password_change",
                "/rest/v1/profiles"
            ],
            handler.RequestPaths);
        Assert.Equal("NewSecure#456", handler.UpdatedPassword);
    }

    [Fact]
    public async Task DirectSupabasePasswordChange_RpcFailureReportsPartialStateAndDoesNotRefreshProfile()
    {
        var userId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var account = StudentAccount(
            userId,
            organizationId,
            Guid.NewGuid(),
            mustChangePassword: true);
        var handler = new PasswordFlowHandler(userId, organizationId)
        {
            RpcCompleted = false
        };
        var client = PublicCloud(handler, organizationId);

        var error = await Assert.ThrowsAsync<PublicCloudApiException>(() =>
            client.ChangeOwnPasswordAsync(
                account,
                "Temporary#123",
                "NewSecure#456",
                "NewSecure#456",
                account.DeviceId,
                default));

        Assert.Equal(ErrorCodes.PasswordChangeFailed, error.Code);
        Assert.Contains("mật khẩu mới", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/rest/v1/profiles", handler.RequestPaths);
        Assert.False(client.Authenticated);
    }

    private static string FindSource(params string[] relativeSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "frontend",
                "src",
                "ExamTransfer.Desktop",
                Path.Combine(relativeSegments));
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate ExamTransfer.Desktop source: {Path.Combine(relativeSegments)}");
    }

    private static SupabasePublicCloudClient PublicCloud(
        HttpMessageHandler handler,
        Guid organizationId) =>
        new(
            new HttpClient(handler),
            optionsProvider: new FixedPublicCloudRuntimeOptionsProvider(
                new PublicCloudRuntimeOptions(
                    new Uri("https://project.supabase.test"),
                    "sb_publishable_test_key",
                    null,
                    "Test",
                    organizationId)));

    private static CurrentAccountDto StudentAccount(
        Guid userId,
        Guid organizationId,
        Guid sessionId,
        bool mustChangePassword) =>
        new(
            userId,
            "HS001",
            "hs001@students.examtransfer.local",
            "Học sinh",
            "HS001",
            UserRole.Student,
            organizationId.ToString("D"),
            sessionId,
            "device-1",
            DateTimeOffset.UtcNow.AddHours(1),
            new DateOnly(2010, 1, 1),
            mustChangePassword,
            userId.ToString("D"));

    private static async Task ExecutePasswordChangeAsync(
        ChangePasswordViewModel viewModel,
        string currentPassword,
        string newPassword)
    {
        viewModel.CurrentPassword = currentPassword;
        viewModel.NewPassword = newPassword;
        viewModel.ConfirmPassword = newPassword;
        viewModel.ChangePasswordCommand.Execute(null);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (viewModel.IsBusy && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.False(viewModel.IsBusy);
    }

    private static string SessionPath(string scope) =>
        Path.Combine(
            Path.GetTempPath(),
            $"examtransfer-password-gate-{scope}-{Guid.NewGuid():N}",
            "session.bin");

    private static void DeleteSessionDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private static string Jwt(Guid subject, Guid sessionId)
    {
        static string Encode(object value) =>
            Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        return $"{Encode(new { alg = "none" })}.{Encode(new
        {
            sub = subject,
            session_id = sessionId,
            exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
        })}.signature";
    }

    private sealed class PasswordFlowHandler(
        Guid userId,
        Guid organizationId) : HttpMessageHandler
    {
        public List<string> RequestPaths { get; } = [];
        public bool ProfileMustChangePassword { get; set; }
        public HttpStatusCode ProfileStatusCode { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode TokenStatusCode { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode UserUpdateStatusCode { get; set; } = HttpStatusCode.OK;
        public bool RpcCompleted { get; set; } = true;
        public string? UpdatedPassword { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            RequestPaths.Add(path);
            if (path == "/auth/v1/token")
            {
                if (TokenStatusCode != HttpStatusCode.OK)
                    return new HttpResponseMessage(TokenStatusCode);
                return Ok(new
                {
                    access_token = Jwt(userId, Guid.NewGuid()),
                    refresh_token = "refresh-token-redacted",
                    expires_in = 3600,
                    user = new
                    {
                        id = userId.ToString("D"),
                        email = "hs001@students.examtransfer.local"
                    }
                });
            }

            if (path == "/auth/v1/user")
            {
                if (UserUpdateStatusCode != HttpStatusCode.OK)
                    return new HttpResponseMessage(UserUpdateStatusCode);
                var payload = await request.Content!.ReadFromJsonAsync<JsonElement>(
                    cancellationToken);
                UpdatedPassword = payload.GetProperty("password").GetString();
                return Ok(new { id = userId.ToString("D") });
            }

            if (path == "/rest/v1/rpc/complete_own_password_change")
                return Ok(RpcCompleted);

            if (path == "/rest/v1/profiles")
            {
                if (ProfileStatusCode != HttpStatusCode.OK)
                    return new HttpResponseMessage(ProfileStatusCode);
                return Ok(new[]
                {
                    new
                    {
                        id = userId.ToString("D"),
                        organization_id = organizationId.ToString("D"),
                        username = "HS001",
                        display_name = "Học sinh",
                        student_code = "HS001",
                        date_of_birth = "2010-01-01",
                        must_change_password = ProfileMustChangePassword,
                        role = "Student",
                        is_active = true
                    }
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Ok<T>(T body) =>
            new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(body)
            };
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }

    private sealed class NeverAuthentication : IUnifiedAuthenticationService
    {
        public Task<UnifiedLoginResult> LoginAsync(
            string account,
            string password,
            string deviceId,
            string machineName,
            string appVersion,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Authentication is not expected in this test.");
    }
}
