using ExamTransfer.Application;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class CloudOwnershipTests
{
    [Theory]
    [InlineData("exam_sessions")]
    [InlineData("session")]
    [InlineData("classes")]
    [InlineData("exam_files")]
    [InlineData("grades")]
    public void Local_owned_entities_are_pushable(string entityType)
    {
        Assert.True(CloudEntityOwnershipRegistry.MayPushToCloud(
            entityType, "{\"access_mode\":\"PublicCloud\"}"));
    }

    [Theory]
    [InlineData("class_enrollment_requests")]
    [InlineData("public_device_connections")]
    [InlineData("public_device_commands")]
    [InlineData("public_device_command_results")]
    public void Cloud_owned_entities_are_never_pushable(string entityType)
    {
        Assert.False(CloudEntityOwnershipRegistry.MayPushToCloud(entityType, "{}"));
    }

    [Theory]
    [InlineData("session_participants")]
    [InlineData("submissions")]
    [InlineData("submission_files")]
    [InlineData("violations")]
    [InlineData("quiz_attempts")]
    [InlineData("quiz_answers")]
    public void PublicCloud_source_rows_are_not_reverse_pushed(string entityType)
    {
        Assert.False(CloudEntityOwnershipRegistry.MayPushToCloud(
            entityType, "{\"source_mode\":\"PublicCloud\"}"));
        Assert.True(CloudEntityOwnershipRegistry.MayPushToCloud(
            entityType, "{\"source_mode\":\"Lan\"}"));
    }
}
