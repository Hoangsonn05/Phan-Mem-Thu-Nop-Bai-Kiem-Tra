using System.Security.Claims;
using ExamTransfer.Application;
using ExamTransfer.LocalServer.Middleware;
using ExamTransfer.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class StudentSecurityGateTests
{
    [Fact]
    public async Task TemporaryPassword_BlocksNonAuthStudentRequests()
    {
        var called = false;
        var middleware = new PasswordChangeGateMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });
        var context = Context(passwordChangeRequired: true, "/api/v1/classes");

        var error = await Assert.ThrowsAsync<ApiException>(() => middleware.InvokeAsync(context));

        Assert.Equal(ErrorCodes.PasswordChangeRequired, error.Code);
        Assert.Equal(403, error.StatusCode);
        Assert.False(called);
    }

    [Theory]
    [InlineData("/api/v1/auth/me")]
    [InlineData("/api/v1/auth/change-password")]
    [InlineData("/api/v1/auth/heartbeat")]
    [InlineData("/api/v1/auth/logout")]
    public async Task TemporaryPassword_AllowsRequiredAuthEndpoints(string path)
    {
        var called = false;
        var middleware = new PasswordChangeGateMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });
        var context = Context(passwordChangeRequired: true, path);

        await middleware.InvokeAsync(context);

        Assert.True(called);
    }

    [Fact]
    public async Task ChangedPassword_AllowsRequestToContinue()
    {
        var called = false;
        var middleware = new PasswordChangeGateMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });
        var context = Context(passwordChangeRequired: false, "/api/v1/sessions/join");

        await middleware.InvokeAsync(context);

        Assert.True(called);
    }

    private static DefaultHttpContext Context(bool passwordChangeRequired, string path)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Role, UserRole.Student.ToString()),
            new Claim("password_change_required", passwordChangeRequired ? "true" : "false")
        ],
        "test");
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
            Request = { Path = path }
        };
    }
}
