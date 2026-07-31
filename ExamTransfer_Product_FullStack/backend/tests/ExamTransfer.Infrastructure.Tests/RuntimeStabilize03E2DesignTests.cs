using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.LocalServer.Workers;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed class RuntimeStabilize03E2DesignTests
{
    [Fact]
    public void ParticipantJoinAndTeacherMonitor_HaveNoCloudRealtimeOrPollingBridge()
    {
        var migration = ReadRootFile(
            "backend/supabase/migrations/20260727122721_session_first_open_request.sql");
        var joinFunction = Between(
            migration,
            "create or replace function public.join_open_public_session_by_room_code(",
            "revoke all on function public.join_open_public_session_by_room_code");
        Assert.Contains("insert into public.session_participants", joinFunction, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("realtime.send", joinFunction, StringComparison.OrdinalIgnoreCase);

        var liveMonitor = ReadRootFile(
            "frontend/src/ExamTransfer.Desktop/ViewModels/LiveMonitorViewModel.cs");
        Assert.Contains("new RealtimeService(AppServices.BaseUrl)", liveMonitor, StringComparison.Ordinal);
        Assert.DoesNotContain("SupabaseRealtimeService", liveMonitor, StringComparison.Ordinal);
        Assert.DoesNotContain("PeriodicTimer", liveMonitor, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcherTimer", liveMonitor, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParticipantPull_CommitsInsertAndUpdate_AdvancesCursorFeedsSnapshotAndEmitsNoPulse()
    {
        await using var database = await PublicCloudTestHarness.CreateDatabaseAsync();
        var primarySeed = await PublicCloudTestHarness.SeedSessionAsync(
            database.Context,
            SessionAccessMode.PublicCloud);
        var otherSeed = await PublicCloudTestHarness.SeedSessionAsync(
            database.Context,
            SessionAccessMode.PublicCloud);
        var primarySessionId = primarySeed.SessionId;
        var otherSessionId = otherSeed.SessionId;
        database.Context.SessionParticipantsSet.RemoveRange(primarySeed, otherSeed);
        await database.Context.SaveChangesAsync();

        var participantId = Guid.NewGuid();
        var otherParticipantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var realtime = new RecordingRealtimePublisher();

        await RunPullOnceAsync(
            database.Path,
            new PullCloudAdapter(new Dictionary<string, CloudPullRecord>
            {
                ["session_participants"] = ParticipantRecord(
                    participantId,
                    primarySessionId,
                    101,
                    now,
                    "SV-03E2",
                    "Projected student",
                    ParticipantStatus.PendingApproval)
            }),
            realtime);

        await using (var insertedContext = database.CreateContext())
        {
            var inserted = await insertedContext.SessionParticipantsSet
                .SingleAsync(x => x.Id == participantId);
            Assert.Equal(primarySessionId, inserted.SessionId);
            Assert.Equal("SV-03E2", inserted.StudentCode);
            Assert.Equal("Projected student", inserted.DisplayName);
            Assert.Equal(ParticipantStatus.PendingApproval, inserted.Status);
            Assert.Equal("PublicCloud", inserted.SourceMode);
            Assert.Equal(101, inserted.CloudVersion);
            Assert.Equal(now, inserted.CloudUpdatedAtUtc);
            Assert.Equal(
                101,
                (await insertedContext.PublicCloudPullCursorsSet.SingleAsync(
                    x => x.EntityName == "session_participants")).LastCloudVersion);

            var detail = await PublicCloudTestHarness
                .CreateSessionService(insertedContext, new PullCloudAdapter(
                    new Dictionary<string, CloudPullRecord>()), realtime)
                .GetAsync(primarySessionId, CancellationToken.None);
            var participant = Assert.Single(detail.Participants);
            Assert.Equal(participantId, participant.Id);
            Assert.Equal(SessionAccessMode.PublicCloud, detail.Summary.AccessMode);
        }

        await RunPullOnceAsync(
            database.Path,
            new PullCloudAdapter(new Dictionary<string, CloudPullRecord>
            {
                ["session_participants"] = ParticipantRecord(
                    otherParticipantId,
                    otherSessionId,
                    102,
                    now.AddSeconds(1),
                    "SV-OTHER",
                    "Other session student",
                    ParticipantStatus.Approved)
            }),
            realtime);
        await RunPullOnceAsync(
            database.Path,
            new PullCloudAdapter(new Dictionary<string, CloudPullRecord>
            {
                ["session_participants"] = ParticipantRecord(
                    participantId,
                    primarySessionId,
                    103,
                    now.AddSeconds(2),
                    "SV-03E2",
                    "Updated projected student",
                    ParticipantStatus.Approved)
            }),
            realtime);
        await RunPullOnceAsync(
            database.Path,
            new PullCloudAdapter(new Dictionary<string, CloudPullRecord>
            {
                ["session_participants"] = ParticipantRecord(
                    participantId,
                    primarySessionId,
                    102,
                    now.AddSeconds(3),
                    "SV-03E2",
                    "Stale participant payload",
                    ParticipantStatus.Rejected)
            }),
            realtime);

        await using var verify = database.CreateContext();
        var updated = await verify.SessionParticipantsSet.SingleAsync(x => x.Id == participantId);
        Assert.Equal("Updated projected student", updated.DisplayName);
        Assert.Equal(ParticipantStatus.Approved, updated.Status);
        Assert.Equal(103, updated.CloudVersion);
        Assert.Equal(
            103,
            (await verify.PublicCloudPullCursorsSet.SingleAsync(
                x => x.EntityName == "session_participants")).LastCloudVersion);

        var sessionService = PublicCloudTestHarness.CreateSessionService(
            verify,
            new PullCloudAdapter(new Dictionary<string, CloudPullRecord>()),
            realtime);
        var primaryDetail = await sessionService.GetAsync(primarySessionId, CancellationToken.None);
        var primaryParticipant = Assert.Single(primaryDetail.Participants);
        Assert.Equal(participantId, primaryParticipant.Id);
        Assert.DoesNotContain(primaryDetail.Participants, x => x.Id == otherParticipantId);

        var otherDetail = await sessionService.GetAsync(otherSessionId, CancellationToken.None);
        Assert.Equal(otherParticipantId, Assert.Single(otherDetail.Participants).Id);
        Assert.Empty(realtime.Events);
    }

    private static CloudPullRecord ParticipantRecord(
        Guid participantId,
        Guid sessionId,
        long cloudVersion,
        DateTimeOffset updatedAt,
        string studentCode,
        string displayName,
        ParticipantStatus status) =>
        new(
            "session_participants",
            participantId.ToString(),
            cloudVersion,
            updatedAt,
            JsonSerializer.Serialize(new
            {
                id = participantId,
                session_id = sessionId,
                user_id = Guid.NewGuid(),
                student_code = studentCode,
                display_name = displayName,
                class_name = "03E2",
                device_id = $"device-{participantId:N}",
                machine_name = "machine-03e2",
                ip_address = "127.0.0.1",
                app_version = "1.0",
                status = status.ToString(),
                joined_at = updatedAt,
                approved_at = status == ParticipantStatus.Approved ? updatedAt : (DateTimeOffset?)null,
                last_seen_at = updatedAt,
                download_status = "NotStarted",
                submission_status = "NotStarted",
                extra_time_minutes = 0,
                resubmit_allowed = false,
                resubmit_reason = (string?)null,
                capability_json = new { source = "03E2" },
                updated_at = updatedAt,
                cloud_version = cloudVersion
            }));

    private static string Between(string value, string start, string end)
    {
        var startIndex = value.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        Assert.True(startIndex >= 0, $"Missing start marker: {start}");
        var endIndex = value.IndexOf(end, startIndex, StringComparison.OrdinalIgnoreCase);
        Assert.True(endIndex > startIndex, $"Missing end marker: {end}");
        return value[startIndex..endIndex];
    }

    private static string ReadRootFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "ExamTransfer.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(
            directory!.FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static async Task RunPullOnceAsync(
        string databasePath,
        ICloudAdapter cloud,
        IRealtimePublisher realtime)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton(cloud);
        services.AddSingleton(realtime);
        await using var provider = services.BuildServiceProvider();
        var worker = new PublicCloudPullWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PublicCloudPullWorker>.Instance);

        await worker.PullOnceAsync(CancellationToken.None);
    }

    private sealed class RecordingRealtimePublisher : IRealtimePublisher
    {
        public List<(Guid SessionId, string EventName)> Events { get; } = [];

        public Task PublishSessionAsync<T>(
            Guid sessionId,
            string eventName,
            long sequence,
            T payload,
            CancellationToken cancellationToken = default)
        {
            Events.Add((sessionId, eventName));
            return Task.CompletedTask;
        }

        public Task PublishParticipantAsync<T>(
            Guid sessionId,
            Guid participantId,
            string eventName,
            long sequence,
            T payload,
            CancellationToken cancellationToken = default)
        {
            Events.Add((sessionId, eventName));
            return Task.CompletedTask;
        }
    }
}
