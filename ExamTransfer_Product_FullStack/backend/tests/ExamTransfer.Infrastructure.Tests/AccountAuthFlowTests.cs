using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Security;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class AccountAuthFlowTests
{
    private const string TestOrganizationId = "516543f3-ca00-480e-87ca-683243ffdc0b";

    // Supabase Auth always issues UUID user ids. ProvisionUserAsync enforces this
    // (Guid.TryParse), so test doubles must use valid GUIDs, not string labels.
    private const string TeacherProviderId = "11111111-1111-4111-8111-111111111111";
    private const string StudentProviderId = "22222222-2222-4222-8222-222222222222";
    private const string Teacher2ProviderId = "12121212-1212-4121-8121-121212121212";
    private const string Teacher3ProviderId = "33333333-3333-4333-8333-333333333333";
    private const string InactiveProviderId = "44444444-4444-4444-8444-444444444444";
    private const string PasswordStudentProviderId = "55555555-5555-4555-8555-555555555555";

    [Fact]
    public async Task TeacherLogin_IssuesAccountTokenAndSession()
    {
        await using var database = await TestDatabase.CreateAsync();
        var teacher = new User
        {
            Username = "teacher1",
            DisplayName = "Teacher One",
            Email = "teacher@example.test",
            Role = UserRole.Teacher,
            SupabaseAuthUserId = TeacherProviderId,
            OrganizationId = "org-1"
        };
        database.Context.UsersSet.Add(teacher);
        await database.Context.SaveChangesAsync();

        var harness = CreateHarness(database.Context, new StaticIdentityProvider(TeacherProviderId, "teacher1", "teacher@example.test"));
        var result = await harness.Auth.LoginAsync(LoginRequest("teacher1"), "127.0.0.1", CancellationToken.None);

        Assert.True(result.Authenticated);
        Assert.False(result.RequiresStudentConfirmation);
        Assert.Equal(UserRole.Teacher, result.Role);
        Assert.NotNull(result.AccessToken);

        var principal = harness.Tokens.ValidateAccountToken(result.AccessToken!);
        Assert.NotNull(principal);
        Assert.Equal(teacher.Id, principal.UserId);
        Assert.Equal("device-1", principal.DeviceId);

        var session = await database.Context.UserLoginSessionsSet.SingleAsync();
        Assert.Equal(teacher.Id, session.UserId);
        Assert.False(session.SessionTokenHash.StartsWith("pending:", StringComparison.Ordinal));
        Assert.Null(session.RevokedAtUtc);
    }

    [Fact]
    public async Task StudentLogin_IssuesTokenWithoutIdentityChallenge()
    {
        await using var database = await TestDatabase.CreateAsync();
        var harness = CreateHarness(
            database.Context,
            new StaticIdentityProvider(
                StudentProviderId,
                "23174800110",
                "23174800110@students.examtransfer.local",
                UserRole.Student));

        var result = await harness.Auth.LoginAsync(
            LoginRequest("23174800110"),
            "127.0.0.1",
            CancellationToken.None);

        Assert.True(result.Authenticated);
        Assert.False(result.RequiresStudentConfirmation);
        Assert.Null(result.ChallengeToken);
        Assert.Equal(UserRole.Student, result.Role);
        Assert.Equal("23174800110", result.StudentCode);
        Assert.NotNull(result.AccessToken);
        Assert.NotNull(harness.Tokens.ValidateAccountToken(result.AccessToken!));

        var current = await harness.Auth.GetCurrentAsync(
            harness.Tokens.ValidateAccountToken(result.AccessToken!)!,
            CancellationToken.None);
        Assert.Equal(new DateOnly(2005, 5, 9), current.DateOfBirth);
        Assert.True(current.MustChangePassword);
    }

    [Fact]
    public async Task StudentPasswordChange_UpdatesSupabaseAndLocalProfileFlag()
    {
        await using var database = await TestDatabase.CreateAsync();
        var provider = new PasswordChangingIdentityProvider();
        var harness = CreateHarness(database.Context, provider);
        var login = await harness.Auth.LoginAsync(
            LoginRequest("23174800110"),
            "127.0.0.1",
            CancellationToken.None);
        var principal = harness.Tokens.ValidateAccountToken(login.AccessToken!);

        var result = await harness.Auth.ChangePasswordAsync(
            principal!,
            new ChangePasswordRequest("correct-password", "Safe@2026X", "Safe@2026X"),
            CancellationToken.None);

        Assert.True(result.Changed);
        Assert.False(result.MustChangePassword);
        Assert.Equal(1, provider.ChangeCalls);
        Assert.Equal("Safe@2026X", provider.LastNewPassword);

        var user = await database.Context.UsersSet.SingleAsync();
        Assert.False(user.MustChangePassword);
        var current = await harness.Auth.GetCurrentAsync(principal!, CancellationToken.None);
        Assert.False(current.MustChangePassword);
    }

    [Fact]
    public async Task StudentPasswordChange_RejectsWeakPasswordBeforeCallingSupabase()
    {
        await using var database = await TestDatabase.CreateAsync();
        var provider = new PasswordChangingIdentityProvider();
        var harness = CreateHarness(database.Context, provider);
        var login = await harness.Auth.LoginAsync(
            LoginRequest("23174800110"),
            null,
            CancellationToken.None);
        var principal = harness.Tokens.ValidateAccountToken(login.AccessToken!);

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            harness.Auth.ChangePasswordAsync(
                principal!,
                new ChangePasswordRequest("correct-password", "uneti", "uneti"),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.PasswordPolicyRejected, error.Code);
        Assert.Equal(0, provider.ChangeCalls);
    }

    [Fact]
    public async Task AccountSession_BlocksSecondDeviceAndAllowsSameDeviceResume()
    {
        await using var database = await TestDatabase.CreateAsync();
        var user = new User
        {
            Username = "teacher2",
            DisplayName = "Teacher Two",
            Role = UserRole.Teacher,
            SupabaseAuthUserId = Teacher2ProviderId,
            OrganizationId = "org-1"
        };
        database.Context.UsersSet.Add(user);
        await database.Context.SaveChangesAsync();

        var options = Options.Create(TestOptions());
        var sessions = new AccountSessionService(database.Context, options);
        var first = await sessions.ClaimAsync(user, "device-1", "machine-1", null, null, CancellationToken.None);
        var resumed = await sessions.ClaimAsync(user, "device-1", "machine-1b", null, null, CancellationToken.None);

        Assert.Equal(first.Id, resumed.Id);

        var conflict = await Assert.ThrowsAsync<ApiException>(() =>
            sessions.ClaimAsync(user, "device-2", "machine-2", null, null, CancellationToken.None));
        Assert.Equal(ErrorCodes.AccountAlreadyActive, conflict.Code);
        Assert.Equal(409, conflict.StatusCode);
    }

    [Fact]
    public async Task HeartbeatAndLogout_UpdateThenRevokeSession()
    {
        await using var database = await TestDatabase.CreateAsync();
        var teacher = new User
        {
            Username = "teacher3",
            DisplayName = "Teacher Three",
            Role = UserRole.Teacher,
            SupabaseAuthUserId = Teacher3ProviderId,
            OrganizationId = "org-1"
        };
        database.Context.UsersSet.Add(teacher);
        await database.Context.SaveChangesAsync();

        var harness = CreateHarness(database.Context, new StaticIdentityProvider(Teacher3ProviderId, "teacher3", null));
        var login = await harness.Auth.LoginAsync(LoginRequest("teacher3"), null, CancellationToken.None);
        var principal = harness.Tokens.ValidateAccountToken(login.AccessToken!);
        Assert.NotNull(principal);

        var heartbeat = await harness.Auth.HeartbeatAsync(
            principal!,
            new AccountHeartbeatRequest("device-1", "machine-updated", DateTimeOffset.UtcNow),
            CancellationToken.None);
        Assert.True(heartbeat.Active);
        Assert.True(heartbeat.LeaseExpiresAtUtc > heartbeat.ServerNowUtc);

        await harness.Auth.LogoutAsync(principal!, new LogoutRequest("device-1", "test"), CancellationToken.None);
        await harness.Auth.LogoutAsync(principal!, new LogoutRequest("device-1", "test-again"), CancellationToken.None);

        var validation = await harness.Sessions.ValidateAsync(principal!, CancellationToken.None);
        Assert.Null(validation);
        var session = await database.Context.UserLoginSessionsSet.SingleAsync();
        Assert.Equal("test", session.RevokeReason);
        Assert.NotNull(session.RevokedAtUtc);
    }

    [Fact]
    public void AccountToken_RejectsTamperedAndExpiredTokens()
    {
        var tokens = new AccountTokenService(Options.Create(TestOptions()));
        var issued = tokens.IssueAccountToken(Guid.NewGuid(), Guid.NewGuid(), UserRole.Teacher, "org-1", "device-1", TimeSpan.FromMinutes(5));
        var principal = tokens.ValidateAccountToken(issued.Token);

        Assert.NotNull(principal);

        var replacement = issued.Token[^1] == 'A' ? 'B' : 'A';
        var tampered = issued.Token[..^1] + replacement;
        Assert.Null(tokens.ValidateAccountToken(tampered));

        var expired = tokens.IssueAccountToken(Guid.NewGuid(), Guid.NewGuid(), UserRole.Teacher, "org-1", "device-1", TimeSpan.FromSeconds(-1));
        Assert.Null(tokens.ValidateAccountToken(expired.Token));
    }

    [Fact]
    public void AccountToken_SecurityCases()
    {
        var tokens = new AccountTokenService(Options.Create(TestOptions()));

        // Token null/rỗng/whitespace trả null, không ném exception
        Assert.Null(tokens.ValidateAccountToken(null!));
        Assert.Null(tokens.ValidateAccountToken(string.Empty));
        Assert.Null(tokens.ValidateAccountToken("   "));

        // Token thiếu segment (không có dấu '.')
        Assert.Null(tokens.ValidateAccountToken("onlyonepart"));

        // Token có segment thừa (3 phần)
        var valid = tokens.IssueAccountToken(Guid.NewGuid(), Guid.NewGuid(), UserRole.Teacher, "org-1", "device-1", TimeSpan.FromMinutes(5));
        Assert.Null(tokens.ValidateAccountToken(valid.Token + ".extra"));

        // Token có segment rỗng (dấu '.' đôi)
        Assert.Null(tokens.ValidateAccountToken(".." ));
        Assert.Null(tokens.ValidateAccountToken("body."));
        Assert.Null(tokens.ValidateAccountToken(".sig"));

        // Base64Url không hợp lệ trong signature
        var parts = valid.Token.Split('.');
        Assert.Null(tokens.ValidateAccountToken(parts[0] + ".not!valid@base64#url"));

        // Chữ ký sai độ dài (quá ngắn)
        Assert.Null(tokens.ValidateAccountToken(parts[0] + ".AAAA"));

        // Sửa payload (giữ signature, thay đổi body)
        var tamperedPayload = "AAAA." + parts[1];
        Assert.Null(tokens.ValidateAccountToken(tamperedPayload));

        // Sửa signature (giữ payload, thay đổi signature)
        var altSig = parts[1][^1] == 'Q' ? parts[1][..^1] + "w" : parts[1][..^1] + "Q";
        // altSig có thể là non-canonical hoặc canonical nhưng sai bytes — cả hai phải bị từ chối
        Assert.Null(tokens.ValidateAccountToken(parts[0] + "." + altSig));

        // Non-canonical Base64Url trong signature (bug đã được vá):
        // Tìm token có ký tự cuối là 'A' (nếu không phải, tìm lần khác)
        // 'A' và 'B' encode cùng bytes cho 43-char base64url (trailing bits = 0)
        IssuedToken? canonicalTest = null;
        for (var i = 0; i < 500; i++)
        {
            var t = tokens.IssueAccountToken(Guid.NewGuid(), Guid.NewGuid(), UserRole.Teacher, "org-1", "device-1", TimeSpan.FromMinutes(5));
            if (t.Token[^1] == 'A')
            {
                canonicalTest = t;
                break;
            }
        }
        if (canonicalTest is not null)
        {
            // Thay 'A' bằng 'B' — cùng bytes nhưng non-canonical → phải bị từ chối
            var nonCanonical = canonicalTest.Token[..^1] + 'B';
            Assert.Null(tokens.ValidateAccountToken(nonCanonical));
        }

        // Token hết hạn đúng tại ranh giới: lifetime = 0 (expires <= now)
        var exactExpiry = tokens.IssueAccountToken(Guid.NewGuid(), Guid.NewGuid(), UserRole.Teacher, "org-1", "device-1", TimeSpan.Zero);
        Assert.Null(tokens.ValidateAccountToken(exactExpiry.Token));

        // Token hết hạn rõ ràng (lifetime âm)
        var expired = tokens.IssueAccountToken(Guid.NewGuid(), Guid.NewGuid(), UserRole.Teacher, "org-1", "device-1", TimeSpan.FromSeconds(-1));
        Assert.Null(tokens.ValidateAccountToken(expired.Token));

        // Token còn hạn được chấp nhận (5 phút)
        var still = tokens.IssueAccountToken(Guid.NewGuid(), Guid.NewGuid(), UserRole.Teacher, "org-1", "device-1", TimeSpan.FromMinutes(5));
        Assert.NotNull(tokens.ValidateAccountToken(still.Token));

        // Payload rác (valid base64url nhưng không phải JSON) → trả null, không ném exception
        var garbagePayload = "dGhpcyBpcyBub3QganNvbg"; // base64url của "this is not json"
        var garbageSig = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"; // 43 zeros (sẽ sai signature, chỉ test decode)
        Assert.Null(tokens.ValidateAccountToken(garbagePayload + "." + garbageSig));
    }

    [Fact]
    public async Task InactiveAccount_IsRejected()
    {
        await using var database = await TestDatabase.CreateAsync();
        database.Context.UsersSet.Add(new User
        {
            Username = "inactive",
            DisplayName = "Inactive User",
            Role = UserRole.Teacher,
            SupabaseAuthUserId = InactiveProviderId,
            IsActive = false
        });
        await database.Context.SaveChangesAsync();

        var harness = CreateHarness(database.Context, new StaticIdentityProvider(InactiveProviderId, "inactive", null, UserRole.Teacher, false));
        var ex = await Assert.ThrowsAsync<ApiException>(() => harness.Auth.LoginAsync(LoginRequest("inactive"), null, CancellationToken.None));

        Assert.Equal(ErrorCodes.AccountInactive, ex.Code);
        Assert.Equal(403, ex.StatusCode);
    }

    private static AuthHarness CreateHarness(AppDbContext db, IExternalIdentityProvider provider)
    {
        var options = Options.Create(TestOptions());
        var sessions = new AccountSessionService(db, options);
        var tokens = new AccountTokenService(options);
        var challenges = new LoginChallengeService(new MemoryCache(new MemoryCacheOptions()));
        var protectorRoot = Path.Combine(Path.GetTempPath(), "ExamTransferTests", Guid.NewGuid().ToString("N"));
        var dataProtection = DataProtectionProvider.Create(new DirectoryInfo(protectorRoot));
        var auth = new AccountAuthenticationService(db, provider, sessions, tokens, challenges, dataProtection, options);
        return new AuthHarness(auth, tokens, sessions);
    }

    private static ExamTransferOptions TestOptions() => new()
    {
        Security = new SecurityOptions { TokenSigningKey = "unit-test-signing-key" },
        Auth = new AuthOptions { AccountTokenMinutes = 30, HeartbeatSeconds = 30, LeaseSeconds = 120, ChallengeMinutes = 5 },
        Cloud = new CloudOptions { OrganizationId = TestOrganizationId }
    };

    private static AccountLoginRequest LoginRequest(string account, string deviceId = "device-1") =>
        new(account, "correct-password", deviceId, "machine-1", "test");

    private sealed record AuthHarness(
        AccountAuthenticationService Auth,
        AccountTokenService Tokens,
        AccountSessionService Sessions);

    private sealed class StaticIdentityProvider(
        string providerUserId,
        string account,
        string? email,
        UserRole role = UserRole.Teacher,
        bool isActive = true) : IExternalIdentityProvider
    {
        public Task<ExternalIdentityResult> AuthenticateAsync(AccountLoginRequest request, CancellationToken cancellationToken)
        {
            if (!string.Equals(request.Password, "correct-password", StringComparison.Ordinal))
                throw new ApiException(ErrorCodes.InvalidCredentials, "Invalid credentials.", 401);

            // Real Supabase Auth always returns a verified email; teacher/admin
            // provisioning requires one. Synthesize a default when a caller omits it.
            var resolvedEmail = string.IsNullOrWhiteSpace(email)
                ? $"{account}@teachers.examtransfer.local"
                : email;

            return Task.FromResult(new ExternalIdentityResult(
                providerUserId,
                account,
                resolvedEmail,
                "refresh-token",
                DateTimeOffset.UtcNow.AddHours(1),
                new ExternalApplicationProfile(
                    providerUserId,
                    TestOrganizationId,
                    account,
                    // Supabase profiles always carry a display name regardless of
                    // role; ProvisionUserAsync rejects a blank one for every role.
                    role == UserRole.Student ? "Nguyen Tuan Anh" : account,
                    role == UserRole.Student ? account : null,
                    role.ToString(),
                    isActive,
                    role == UserRole.Student ? new DateOnly(2005, 5, 9) : null,
                    role == UserRole.Student)));
        }
    }

    private sealed class PasswordChangingIdentityProvider : IExternalIdentityProvider, IExternalAccountSecurityService
    {
        public int ChangeCalls { get; private set; }
        public string? LastNewPassword { get; private set; }

        public Task<ExternalIdentityResult> AuthenticateAsync(
            AccountLoginRequest request,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(request.Password, "correct-password", StringComparison.Ordinal))
                throw new ApiException(ErrorCodes.InvalidCredentials, "Invalid credentials.", 401);

            return Task.FromResult(new ExternalIdentityResult(
                PasswordStudentProviderId,
                "23174800110",
                "23174800110@students.examtransfer.local",
                "refresh-token",
                DateTimeOffset.UtcNow.AddHours(1),
                new ExternalApplicationProfile(
                    PasswordStudentProviderId,
                    TestOrganizationId,
                    "23174800110",
                    "Nguyen Tuan Anh",
                    "23174800110",
                    UserRole.Student.ToString(),
                    true,
                    new DateOnly(2005, 5, 9),
                    true)));
        }

        public Task ChangePasswordAsync(
            ExternalPasswordChangeRequest request,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(request.CurrentPassword, "correct-password", StringComparison.Ordinal))
                throw new ApiException(ErrorCodes.InvalidCurrentPassword, "Invalid current password.", 401);

            ChangeCalls++;
            LastNewPassword = request.NewPassword;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task StudentProfile_WithTeacherRoleMismatch_IsRejected()
    {
        var misassignedProviderId = "99999999-9999-4999-8999-999999999999";
        var mockProvider = new MisassignedRoleIdentityProvider(misassignedProviderId, "23174800117");
        await using var database = await TestDatabase.CreateAsync();
        var harness = CreateHarness(database.Context, mockProvider);

        var ex = await Assert.ThrowsAsync<ApiException>(() => harness.Auth.LoginAsync(
            LoginRequest("23174800117"),
            "127.0.0.1",
            CancellationToken.None));

        Assert.Equal(ErrorCodes.AuthProfileRoleInconsistent, ex.Code);
        Assert.Contains(
            "Hồ sơ tài khoản không nhất quán",
            ex.Message,
            StringComparison.Ordinal);
        Assert.Equal(403, ex.StatusCode);
    }

    [Fact]
    public async Task ValidProfile_SyncsRole_WhenLocalUserHasDifferentRole()
    {
        await using var database = await TestDatabase.CreateAsync();
        // Local user is Teacher
        var user = new User
        {
            Username = "23174800120",
            DisplayName = "Test User",
            Email = "23174800120@students.examtransfer.local",
            Role = UserRole.Teacher,
            SupabaseAuthUserId = "88888888-8888-4888-8888-888888888888",
            OrganizationId = TestOrganizationId
        };
        database.Context.UsersSet.Add(user);
        await database.Context.SaveChangesAsync();

        // But Supabase says Student (which is authoritative)
        var harness = CreateHarness(
            database.Context,
            new StaticIdentityProvider(
                "88888888-8888-4888-8888-888888888888",
                "23174800120",
                "23174800120@students.examtransfer.local",
                UserRole.Student));

        var result = await harness.Auth.LoginAsync(
            LoginRequest("23174800120"),
            "127.0.0.1",
            CancellationToken.None);

        Assert.True(result.Authenticated);
        Assert.Equal(UserRole.Student, result.Role);

        // Verify local user was updated
        var updatedUser = await database.Context.UsersSet.SingleAsync();
        Assert.Equal(UserRole.Student, updatedUser.Role);
    }

    private sealed class MisassignedRoleIdentityProvider(string providerUserId, string studentCode) : IExternalIdentityProvider
    {
        public Task<ExternalIdentityResult> AuthenticateAsync(AccountLoginRequest request, CancellationToken cancellationToken)
        {
            var profile = new ExternalApplicationProfile(
                providerUserId,
                TestOrganizationId,
                studentCode,
                "Hoc Sinh Test",
                studentCode,
                "Teacher",
                true,
                new DateOnly(2005, 5, 9),
                false);
            return Task.FromResult(new ExternalIdentityResult(
                providerUserId,
                studentCode,
                $"{studentCode}@students.examtransfer.local",
                "refresh-token",
                DateTimeOffset.UtcNow.AddHours(1),
                profile,
                "mock-access-token"));
        }
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private TestDatabase(SqliteConnection connection, AppDbContext context)
        {
            this.connection = connection;
            Context = context;
        }

        public AppDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new AppDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
