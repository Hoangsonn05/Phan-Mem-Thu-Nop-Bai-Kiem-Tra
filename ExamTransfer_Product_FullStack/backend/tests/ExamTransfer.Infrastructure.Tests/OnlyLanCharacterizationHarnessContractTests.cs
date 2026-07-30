using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

/// <summary>
/// Prevents the published OnlyLAN characterization harness from silently
/// shrinking back to a discovery/join-only smoke test.
/// </summary>
public sealed class OnlyLanCharacterizationHarnessContractTests
{
    [Fact]
    public void Fixture_IsExplicitlyTestingOnly_AndSeedsAValidStrictQuiz()
    {
        var migrator = PublicCloudTestHarness.ReadRepositoryFile(
            "backend/src/ExamTransfer.DbMigrator/Program.cs");

        Assert.Contains("--seed-onlylan-e2e-fixture", migrator, StringComparison.Ordinal);
        Assert.Contains("EnsureTestingFixtureAllowed", migrator, StringComparison.Ordinal);
        Assert.Contains("DOTNET_ENVIRONMENT", migrator, StringComparison.Ordinal);
        Assert.Contains("EXAMTRANSFER_ALLOW_TEST_FIXTURE", migrator, StringComparison.Ordinal);
        Assert.Contains("SessionAccessMode.LanOnly", migrator, StringComparison.Ordinal);
        Assert.Contains("SessionAdmissionMode.OpenRequest", migrator, StringComparison.Ordinal);
        Assert.Contains("ExamDeliveryType.MultipleChoice", migrator, StringComparison.Ordinal);
        Assert.Contains("SupervisionMode.Standard", migrator, StringComparison.Ordinal);
        Assert.Contains("QuizResultPolicy.ShowAfterSubmission", migrator, StringComparison.Ordinal);
        Assert.Contains("AutoApprove = false", migrator, StringComparison.Ordinal);
        Assert.Contains("Points = 5.00m", migrator, StringComparison.Ordinal);
        Assert.Equal(2, migrator.Split("CreateQuizQuestion(exam,", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void PublishedHarness_CoversTheCompleteLifecycle_AndDisablesCloud()
    {
        var script = PublicCloudTestHarness.ReadRepositoryFile(
            "backend/../scripts/test-published-onlylan-e2e.ps1");

        Assert.Contains("EXAMTRANSFER_ALLOW_TEST_FIXTURE", script, StringComparison.Ordinal);
        Assert.Contains("Cloud__Enabled' = 'false'", script, StringComparison.Ordinal);
        Assert.Contains("EXAMTRANSFER_Cloud__Enabled' = 'false'", script, StringComparison.Ordinal);
        Assert.Contains("Security__TokenSigningKey", script, StringComparison.Ordinal);
        Assert.Contains("EXAMTRANSFER_Security__TokenSigningKey", script, StringComparison.Ordinal);
        Assert.Contains("Authorization = \"Bearer $($handoff.studentAccountToken)\"", script, StringComparison.Ordinal);
        Assert.Contains("X-Exam-Session-Token", script, StringComparison.Ordinal);
        Assert.Contains("LAN_E2E_DISCOVERY", script, StringComparison.Ordinal);
        Assert.Contains("LAN_E2E_JOIN_PENDING", script, StringComparison.Ordinal);
        Assert.Contains("LAN_E2E_APPROVED", script, StringComparison.Ordinal);
        Assert.Contains("control-policy/apply", script, StringComparison.Ordinal);
        Assert.Contains("LAN_E2E_POLICY_APPLIED", script, StringComparison.Ordinal);
        Assert.Contains("/student/quiz/sessions/", script, StringComparison.Ordinal);
        Assert.Contains("/finalize", script, StringComparison.Ordinal);
        Assert.Contains("LAN_E2E_QUIZ_FINALIZED", script, StringComparison.Ordinal);
        Assert.Contains("/collect", script, StringComparison.Ordinal);
        Assert.Contains("/end", script, StringComparison.Ordinal);
        Assert.Contains("ONLYLAN_E2E_REPEAT_OK", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SignalRTestClient_AcknowledgesTheRealPolicyGate()
    {
        var client = PublicCloudTestHarness.ReadRepositoryFile(
            "backend/tests/ExamTransfer.OnlyLan.TestClient/Program.cs");
        var solution = PublicCloudTestHarness.ReadRepositoryFile("backend/../ExamTransfer.slnx");
        var packages = PublicCloudTestHarness.ReadRepositoryFile(
            "backend/Directory.Packages.props");

        Assert.Contains("ClientReady", client, StringComparison.Ordinal);
        Assert.Contains("PolicyApplyAck", client, StringComparison.Ordinal);
        Assert.Contains("PolicyApplyStatus.Applied", client, StringComparison.Ordinal);
        Assert.Contains("ONLYLAN_POLICY_ACK_OK", client, StringComparison.Ordinal);
        Assert.Contains("X-Exam-Session-Token", client, StringComparison.Ordinal);
        Assert.DoesNotContain("AccessTokenProvider", client, StringComparison.Ordinal);
        Assert.Contains("ExamTransfer.OnlyLan.TestClient.csproj", solution, StringComparison.Ordinal);
        Assert.Contains("Microsoft.AspNetCore.SignalR.Client", packages, StringComparison.Ordinal);
    }
}
