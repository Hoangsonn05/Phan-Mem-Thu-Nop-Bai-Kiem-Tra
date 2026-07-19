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
            SupabaseAuthUserId = "provider-teacher",
            OrganizationId = "org-1"
        };
        database.Context.UsersSet.Add(teacher);
        await database.Context.SaveChangesAsync();

        var harness = CreateHarness(database.Context, new StaticIdentityProvider("provider-teacher", "teacher1", "teacher@example.test"));
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
    public async Task StudentLogin_RequiresChallenge_ThenConfirmIssuesToken()
    {
        await using var database = await TestDatabase.CreateAsync();
        var student = new User
        {
            Username = "student1",
            DisplayName = "Nguyen Van A",
            Email = "student@example.test",
            StudentCode = "SV001",
            Role = UserRole.Student,
            SupabaseAuthUserId = "provider-student",
            OrganizationId = "org-1"
        };
        database.Context.UsersSet.Add(student);
        await database.Context.SaveChangesAsync();

        var harness = CreateHarness(database.Context, new StaticIdentityProvider("provider-student", "student1", "student@example.test", UserRole.Student));
        var firstStep = await harness.Auth.LoginAsync(LoginRequest("student1"), "127.0.0.1", CancellationToken.None);

        Assert.False(firstStep.Authenticated);
        Assert.True(firstStep.RequiresStudentConfirmation);
        Assert.NotNull(firstStep.ChallengeToken);
        Assert.Null(firstStep.AccessToken);

        var mismatch = await Assert.ThrowsAsync<ApiException>(() =>
            harness.Auth.ConfirmStudentAsync(
                new StudentIdentityConfirmRequest(firstStep.ChallengeToken!, "SV001", "Wrong Name", "device-1", "machine-1"),
                "127.0.0.1",
                CancellationToken.None));
        Assert.Equal(ErrorCodes.StudentIdentityMismatch, mismatch.Code);
        Assert.Equal(422, mismatch.StatusCode);

        var secondStep = await harness.Auth.LoginAsync(LoginRequest("student1"), "127.0.0.1", CancellationToken.None);
        var confirmed = await harness.Auth.ConfirmStudentAsync(
            new StudentIdentityConfirmRequest(secondStep.ChallengeToken!, "sv001", "nguyen van a", "device-1", "machine-1"),
            "127.0.0.1",
            CancellationToken.None);

        Assert.True(confirmed.Authenticated);
        Assert.False(confirmed.RequiresStudentConfirmation);
        Assert.Equal(UserRole.Student, confirmed.Role);
        Assert.NotNull(confirmed.AccessToken);
        Assert.NotNull(harness.Tokens.ValidateAccountToken(confirmed.AccessToken!));
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
            SupabaseAuthUserId = "provider-teacher-2",
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
            SupabaseAuthUserId = "provider-teacher-3",
            OrganizationId = "org-1"
        };
        database.Context.UsersSet.Add(teacher);
        await database.Context.SaveChangesAsync();

        var harness = CreateHarness(database.Context, new StaticIdentityProvider("provider-teacher-3", "teacher3", null));
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
            SupabaseAuthUserId = "provider-inactive",
            IsActive = false
        });
        await database.Context.SaveChangesAsync();

        var harness = CreateHarness(database.Context, new StaticIdentityProvider("provider-inactive", "inactive", null, UserRole.Teacher, false));
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

            return Task.FromResult(new ExternalIdentityResult(
                providerUserId,
                account,
                email,
                "refresh-token",
                DateTimeOffset.UtcNow.AddHours(1),
                new ExternalApplicationProfile(
                    providerUserId,
                    TestOrganizationId,
                    account,
                    null,
                    null,
                    role.ToString(),
                    isActive)));
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
