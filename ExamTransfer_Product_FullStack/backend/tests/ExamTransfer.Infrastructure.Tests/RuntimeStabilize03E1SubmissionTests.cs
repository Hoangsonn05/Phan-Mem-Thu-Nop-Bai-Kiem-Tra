using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ExamTransfer.Infrastructure.Tests;

public sealed partial class RuntimeRebase03AR2SubmissionCharacterizationTests
{
    [Theory]
    [InlineData(SessionStatus.InProgress)]
    [InlineData(SessionStatus.Collecting)]
    public async Task InitWhileSessionAcceptsSubmissions_Succeeds(
        SessionStatus status)
    {
        await using var database = await RuntimeDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context, status);
        var initialized = await CreateService(database.Context).Service.InitAsync(
            Request(seed, $"open-{status}-{Guid.NewGuid():N}"),
            CancellationToken.None);

        Assert.Equal(1, initialized.AttemptNumber);
        Assert.Single(await database.Context.SubmissionsSet.ToListAsync());
    }

    [Theory]
    [InlineData(SessionStatus.Finished)]
    [InlineData(SessionStatus.Cancelled)]
    [InlineData(SessionStatus.Archived)]
    public async Task UploadChunkAfterTerminalSession_IsRejectedWithoutWriting(
        SessionStatus terminal)
    {
        await using var database = await RuntimeDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context, SessionStatus.InProgress);
        var harness = CreateService(database.Context);
        var initialized = await harness.Service.InitAsync(
            Request(seed, $"chunk-terminal-{terminal}-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var file = await database.Context.SubmissionFilesSet
            .SingleAsync(x => x.Id == initialized.FilePlans.Single().FileId);
        seed.Session.Status = terminal;
        seed.Session.EndedAtUtc = DateTimeOffset.UtcNow;
        await database.Context.SaveChangesAsync();

        await using var content = new MemoryStream(EmptyZip, writable: false);
        var error = await Assert.ThrowsAsync<ApiException>(() =>
            harness.Service.UploadChunkAsync(
                initialized.SubmissionId,
                file.Id,
                0,
                content,
                content.Length,
                null,
                CancellationToken.None));

        database.Context.ChangeTracker.Clear();
        var persisted = await database.Context.SubmissionsSet
            .Include(x => x.Participant)
            .SingleAsync(x => x.Id == initialized.SubmissionId);
        Assert.Equal(ErrorCodes.SessionSubmissionNotOpen, error.Code);
        Assert.False(Directory.Exists(file.TemporaryPath));
        Assert.Equal(SubmissionStatus.Uploading, persisted.Status);
        Assert.Equal(SubmissionStatus.Uploading, persisted.Participant.SubmissionStatus);
        Assert.Null(persisted.ReceiptCode);
    }

    [Theory]
    [InlineData(SessionStatus.Finished)]
    [InlineData(SessionStatus.Cancelled)]
    [InlineData(SessionStatus.Archived)]
    public async Task FinalizeAfterTerminalSession_IsRejectedWithoutReceipt(
        SessionStatus terminal)
    {
        await using var database = await RuntimeDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context, SessionStatus.InProgress);
        var harness = CreateService(database.Context);
        var initialized = await InitializeAndUploadAsync(harness, seed);
        seed.Session.Status = terminal;
        seed.Session.EndedAtUtc = DateTimeOffset.UtcNow;
        await database.Context.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            harness.Service.FinalizeAsync(
                initialized.SubmissionId,
                new("terminal regression"),
                CancellationToken.None));

        database.Context.ChangeTracker.Clear();
        var persisted = await database.Context.SubmissionsSet
            .Include(x => x.Participant)
            .SingleAsync(x => x.Id == initialized.SubmissionId);
        Assert.Equal(ErrorCodes.SessionSubmissionNotOpen, error.Code);
        Assert.Equal(SubmissionStatus.Uploading, persisted.Status);
        Assert.Equal(SubmissionStatus.Uploading, persisted.Participant.SubmissionStatus);
        Assert.Null(persisted.ReceiptCode);
        Assert.False(Directory.Exists(harness.Paths.ReceiptRoot(seed.Session.Id)));
    }

    [Fact]
    public async Task FinalizeBeforeEndThenRetryAfterEnd_ReturnsOriginalReceipt()
    {
        await using var database = await RuntimeDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context, SessionStatus.InProgress);
        var harness = CreateService(database.Context);
        var initialized = await InitializeAndUploadAsync(harness, seed);
        var first = await harness.Service.FinalizeAsync(
            initialized.SubmissionId,
            new("first"),
            CancellationToken.None);
        seed.Session.Status = SessionStatus.Finished;
        seed.Session.EndedAtUtc = DateTimeOffset.UtcNow;
        await database.Context.SaveChangesAsync();

        var repeated = await harness.Service.FinalizeAsync(
            initialized.SubmissionId,
            new("retry after end"),
            CancellationToken.None);

        Assert.Equal(first.ReceiptCode, repeated.ReceiptCode);
        Assert.Equal(first.ReceiptSignature, repeated.ReceiptSignature);
        Assert.Equal(first.ServerReceivedAtUtc, repeated.ServerReceivedAtUtc);
        Assert.Single(Directory.EnumerateFiles(
            harness.Paths.ReceiptRoot(seed.Session.Id),
            "*.json",
            SearchOption.TopDirectoryOnly));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EightConcurrentInitCalls_AreAtomic(bool sameKey)
    {
        await using var database = await RuntimeDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context, SessionStatus.InProgress);
        var sharedKey = $"eight-{Guid.NewGuid():N}";
        database.Context.ChangeTracker.Clear();

        var outcomes = await RunConcurrentInitAsync(
            database,
            Enumerable.Range(0, 8)
                .Select(index => Request(
                    seed.Session.Id,
                    seed.Participant.Id,
                    sameKey ? sharedKey : $"{sharedKey}-{index}"))
                .ToArray());

        if (sameKey)
        {
            Assert.All(outcomes, x => Assert.Null(x.Exception));
            Assert.Single(outcomes.Select(x => x.Response!.SubmissionId).Distinct());
            Assert.Single(outcomes.Select(x => x.Response!.AttemptNumber).Distinct());
            Assert.Single(outcomes.Select(x => x.Response!.FilePlans.Single().FileId).Distinct());
        }
        else
        {
            Assert.Single(outcomes, x => x.Response is not null);
            var conflicts = outcomes.Where(x => x.Exception is not null).ToList();
            Assert.Equal(7, conflicts.Count);
            Assert.All(conflicts, outcome =>
            {
                var error = Assert.IsType<ApiException>(outcome.Exception);
                Assert.Equal(ErrorCodes.SubmissionAlreadyProcessing, error.Code);
            });
        }

        await using var verify = database.CreateContext();
        var persisted = Assert.Single(await verify.SubmissionsSet
            .Include(x => x.Files)
            .Where(x => x.ParticipantId == seed.Participant.Id)
            .ToListAsync());
        Assert.Equal(1, persisted.AttemptNumber);
        Assert.Equal(SubmissionStatus.Uploading, persisted.Status);
        Assert.False(Directory.Exists(persisted.Files.Single().TemporaryPath));
    }

    [Fact]
    public async Task ConcurrentInitForDifferentParticipantsAndSessions_RemainsIndependent()
    {
        await using var database = await RuntimeDatabase.CreateAsync();
        var first = await SeedAsync(database.Context, SessionStatus.InProgress);
        var accountId = Guid.NewGuid();
        first.Participant.UserId = accountId;
        var secondParticipant = NewParticipant(first.Session, accountId);
        var secondSession = new ExamSession
        {
            Exam = first.Session.Exam,
            ExamId = first.Session.ExamId,
            RoomCode = $"RR{Random.Shared.Next(100000, 999999)}",
            HostDeviceId = "runtime-rebase-host-2",
            Status = SessionStatus.InProgress,
            AccessMode = SessionAccessMode.LanOnly,
            DeliveryTypeSnapshot = ExamDeliveryType.FileSubmission,
            ExamVersionSnapshot = first.Session.Exam.Version,
            StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            Sequence = 1
        };
        var sameAccountOtherSession = NewParticipant(secondSession, accountId);
        database.Context.AddRange(secondParticipant, secondSession, sameAccountOtherSession);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        var outcomes = await RunConcurrentInitAsync(
            database,
            [
                Request(first.Session.Id, first.Participant.Id, $"independent-{Guid.NewGuid():N}"),
                Request(first.Session.Id, secondParticipant.Id, $"independent-{Guid.NewGuid():N}"),
                Request(secondSession.Id, sameAccountOtherSession.Id, $"independent-{Guid.NewGuid():N}")
            ]);

        Assert.All(outcomes, x => Assert.Null(x.Exception));
        Assert.Equal(3, outcomes.Select(x => x.Response!.SubmissionId).Distinct().Count());
        await using var verify = database.CreateContext();
        Assert.Equal(3, await verify.SubmissionsSet.CountAsync());
    }

    [Fact]
    public async Task CompletedSubmissionRequiresResubmitAuthority()
    {
        await using var database = await RuntimeDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context, SessionStatus.InProgress);
        var harness = CreateService(database.Context);
        var first = await CompleteFirstAttemptAsync(harness, seed);
        var firstSubmissionId = await database.Context.SubmissionsSet
            .Where(x => x.ParticipantId == seed.Participant.Id)
            .Select(x => x.Id)
            .SingleAsync();

        var denied = await Assert.ThrowsAsync<ApiException>(() =>
            harness.Service.InitAsync(
                Request(seed, $"resubmit-denied-{Guid.NewGuid():N}"),
                CancellationToken.None));
        Assert.Equal(ErrorCodes.ResubmitNotAllowed, denied.Code);

        seed.Participant.ResubmitAllowed = true;
        await database.Context.SaveChangesAsync();
        var second = await harness.Service.InitAsync(
            Request(seed, $"resubmit-allowed-{Guid.NewGuid():N}"),
            CancellationToken.None);

        Assert.Equal(2, second.AttemptNumber);
        Assert.False(seed.Participant.ResubmitAllowed);
        Assert.True(File.Exists(Path.Combine(
            harness.Paths.ReceiptRoot(seed.Session.Id),
            firstSubmissionId.ToString("N") + ".json")));
        Assert.Equal(2, await database.Context.SubmissionsSet.CountAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentResubmit_ConsumesOneAuthorityAndCreatesOneAttempt(
        bool sameKey)
    {
        await using var database = await RuntimeDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context, SessionStatus.InProgress);
        var harness = CreateService(database.Context);
        var first = await CompleteFirstAttemptAsync(harness, seed);
        seed.Participant.ResubmitAllowed = true;
        await database.Context.SaveChangesAsync();
        var sharedKey = $"resubmit-eight-{Guid.NewGuid():N}";
        database.Context.ChangeTracker.Clear();

        var outcomes = await RunConcurrentInitAsync(
            database,
            Enumerable.Range(0, 8)
                .Select(index => Request(
                    seed.Session.Id,
                    seed.Participant.Id,
                    sameKey ? sharedKey : $"{sharedKey}-{index}"))
                .ToArray());

        if (sameKey)
        {
            Assert.All(outcomes, x => Assert.Null(x.Exception));
            Assert.Single(outcomes.Select(x => x.Response!.SubmissionId).Distinct());
        }
        else
        {
            Assert.Single(outcomes, x => x.Response is not null);
            Assert.Equal(7, outcomes.Count(x => x.Exception is ApiException
                { Code: ErrorCodes.SubmissionAlreadyProcessing }));
        }

        await using var verify = database.CreateContext();
        var attempts = await verify.SubmissionsSet
            .Where(x => x.ParticipantId == seed.Participant.Id)
            .OrderBy(x => x.AttemptNumber)
            .ToListAsync();
        Assert.Equal([1, 2], attempts.Select(x => x.AttemptNumber).ToArray());
        Assert.Equal(first.ReceiptCode, attempts[0].ReceiptCode);
        Assert.False((await verify.SessionParticipantsSet
            .SingleAsync(x => x.Id == seed.Participant.Id)).ResubmitAllowed);
    }

    [Fact]
    public async Task ReusingIdempotencyKeyWithDifferentUploadContract_IsTypedConflict()
    {
        await using var database = await RuntimeDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context, SessionStatus.InProgress);
        var service = CreateService(database.Context).Service;
        var key = $"conflicting-contract-{Guid.NewGuid():N}";
        var original = Request(seed, key);
        await service.InitAsync(original, CancellationToken.None);
        var changed = original with
        {
            Files =
            [
                original.Files.Single() with { ClientFileId = "different-client-file" }
            ]
        };

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            service.InitAsync(changed, CancellationToken.None));

        Assert.Equal(ErrorCodes.SubmissionIdempotencyConflict, error.Code);
        Assert.Single(await database.Context.SubmissionsSet.ToListAsync());
    }

    [Fact]
    public async Task FinalizeMissingSubmission_ReturnsTypedNotFound()
    {
        await using var database = await RuntimeDatabase.CreateAsync();
        var service = CreateService(database.Context).Service;

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            service.FinalizeAsync(Guid.NewGuid(), new(null), CancellationToken.None));

        Assert.Equal(ErrorCodes.SubmissionNotFound, error.Code);
        Assert.Equal(404, error.StatusCode);
    }

    private static async Task<InitSubmissionResponse> InitializeAndUploadAsync(
        SubmissionHarness harness,
        Seed seed)
    {
        var initialized = await harness.Service.InitAsync(
            Request(seed, $"initialize-upload-{Guid.NewGuid():N}"),
            CancellationToken.None);
        await using var content = new MemoryStream(EmptyZip, writable: false);
        await harness.Service.UploadChunkAsync(
            initialized.SubmissionId,
            initialized.FilePlans.Single().FileId,
            0,
            content,
            content.Length,
            null,
            CancellationToken.None);
        return initialized;
    }

    private static async Task<FinalizeSubmissionResponse> CompleteFirstAttemptAsync(
        SubmissionHarness harness,
        Seed seed)
    {
        var initialized = await InitializeAndUploadAsync(harness, seed);
        return await harness.Service.FinalizeAsync(
            initialized.SubmissionId,
            new("completed first attempt"),
            CancellationToken.None);
    }

    private static SessionParticipant NewParticipant(
        ExamSession session,
        Guid? userId = null) =>
        new()
        {
            Session = session,
            SessionId = session.Id,
            UserId = userId,
            StudentCode = $"RR-{Guid.NewGuid():N}"[..12],
            DisplayName = "Concurrent Runtime Student",
            DeviceId = $"runtime-device-{Guid.NewGuid():N}",
            MachineName = "runtime-machine",
            AppVersion = "03E1",
            Status = ParticipantStatus.Approved,
            ApprovedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            SubmissionStatus = SubmissionStatus.NotStarted,
            SourceMode = "Lan"
        };

    private static async Task<InitOutcome[]> RunConcurrentInitAsync(
        RuntimeDatabase database,
        IReadOnlyList<InitSubmissionRequest> requests)
    {
        var contexts = requests.Select(_ => database.CreateContext()).ToArray();
        try
        {
            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = requests.Select((request, index) => Task.Run(async () =>
            {
                await start.Task;
                return await CaptureAsync(() => CreateService(contexts[index]).Service
                    .InitAsync(request, CancellationToken.None));
            })).ToArray();
            start.TrySetResult();
            return await Task.WhenAll(tasks);
        }
        finally
        {
            foreach (var context in contexts)
                await context.DisposeAsync();
        }
    }
}
