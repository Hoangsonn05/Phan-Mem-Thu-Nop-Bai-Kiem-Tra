using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class ResubmitAuthorityContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParticipantMapper_ProjectsResubmitAuthorityWithoutChangingOtherFields(
        bool resubmitAllowed)
    {
        var now = new DateTimeOffset(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);
        var participant = new SessionParticipant
        {
            Id = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            StudentCode = "SV001",
            DisplayName = "Student",
            DeviceId = "device-1",
            MachineName = "machine-1",
            IpAddress = "192.168.1.20",
            AppVersion = "1.0.0",
            Status = ParticipantStatus.Approved,
            LastSeenUtc = now,
            DownloadStatus = DownloadStatus.Completed,
            SubmissionStatus = SubmissionStatus.Submitted,
            ExtraTimeMinutes = 15,
            ResubmitAllowed = resubmitAllowed
        };

        var dto = participant.ToDto(now, effectiveDeadlineUtc: now.AddHours(1));

        Assert.Equal(participant.Id, dto.Id);
        Assert.Equal(participant.SessionId, dto.SessionId);
        Assert.Equal(participant.StudentCode, dto.StudentCode);
        Assert.Equal(participant.Status, dto.Status);
        Assert.Equal(participant.SubmissionStatus, dto.SubmissionStatus);
        Assert.Equal(participant.ExtraTimeMinutes, dto.ExtraTimeMinutes);
        Assert.Equal(resubmitAllowed, dto.ResubmitAllowed);

        var json = JsonSerializer.Serialize(
            dto,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            resubmitAllowed,
            document.RootElement.GetProperty("resubmitAllowed").GetBoolean());
    }

    [Fact]
    public void PublicTimelineMigration_ChangesOnlyTheAuthorityProjection()
    {
        var historical = PublicCloudTestHarness.ReadRepositoryFile(
            "backend/supabase/migrations/20260727122721_session_first_open_request.sql");
        var migration = PublicCloudTestHarness.ReadRepositoryFile(
            "backend/supabase/migrations/20260731113609_public_student_timeline_resubmit_allowed.sql");
        var historicalFunction = ExtractTimelineContract(historical);
        var replacementFunction = ExtractTimelineContract(migration);

        Assert.Contains(
            "'resubmitAllowed', p.resubmit_allowed,",
            replacementFunction,
            StringComparison.Ordinal);
        var normalizedReplacement = Normalize(replacementFunction).Replace(
            "    'resubmitAllowed', p.resubmit_allowed,\n",
            string.Empty,
            StringComparison.Ordinal);
        Assert.Equal(Normalize(historicalFunction), normalizedReplacement);
        Assert.DoesNotContain("create policy", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alter policy", migration, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractTimelineContract(string sql)
    {
        const string start =
            "create or replace function public.get_public_student_timeline(p_session_id uuid)";
        const string grant =
            "grant execute on function public.get_public_student_timeline(uuid) to authenticated;";
        var startIndex = sql.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        Assert.True(startIndex >= 0, "Timeline function definition is missing.");
        var grantIndex = sql.IndexOf(grant, startIndex, StringComparison.OrdinalIgnoreCase);
        Assert.True(grantIndex >= 0, "Timeline function grant is missing.");
        return sql[startIndex..(grantIndex + grant.Length)];
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
