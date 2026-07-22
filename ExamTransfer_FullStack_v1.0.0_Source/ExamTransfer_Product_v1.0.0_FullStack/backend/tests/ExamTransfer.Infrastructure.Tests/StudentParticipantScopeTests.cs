using System.Security.Claims;
using ExamTransfer.LocalServer.Auth;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class StudentParticipantScopeTests
{
    [Fact]
    public void IsValid_RequiresMatchingAccountAndParticipantUser()
    {
        var userId = Guid.NewGuid();
        var principal = Principal(userId, userId);

        Assert.True(StudentParticipantScope.IsValid(principal));
        Assert.False(StudentParticipantScope.IsValid(Principal(userId, Guid.NewGuid())));
    }

    [Fact]
    public void IsValid_RejectsEitherTokenAloneOrTemporaryPassword()
    {
        var userId = Guid.NewGuid();
        var complete = Principal(userId, userId);

        Assert.False(StudentParticipantScope.IsValid(new ClaimsPrincipal(complete.Identities.Take(1))));
        Assert.False(StudentParticipantScope.IsValid(new ClaimsPrincipal(complete.Identities.Skip(1))));
        Assert.False(StudentParticipantScope.IsValid(Principal(userId, userId, passwordChanged: false)));
    }

    private static ClaimsPrincipal Principal(Guid accountUserId, Guid participantUserId, bool passwordChanged = true)
    {
        var account = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, accountUserId.ToString()),
            new Claim(ClaimTypes.Role, UserRole.Student.ToString()),
            new Claim("password_change_required", passwordChanged ? "false" : "true")
        ], ExamTransferAuthSchemes.Account);
        var participant = new ClaimsIdentity(
        [
            new Claim("user_id", participantUserId.ToString()),
            new Claim("session_id", Guid.NewGuid().ToString()),
            new Claim("participant_id", Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, UserRole.Student.ToString())
        ], ExamTransferAuthSchemes.ExamParticipant);
        return new ClaimsPrincipal([account, participant]);
    }
}
