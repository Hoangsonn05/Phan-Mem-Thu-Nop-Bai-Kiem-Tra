using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Windows;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Infrastructure;

public sealed class SubmissionRecoveryService(
    AppAuthSessionState authState,
    StudentSessionState sessionState,
    ILanDiscoveryService discovery) : ISubmissionRecoveryService
{
    private static readonly TimeSpan[] RetrySchedule =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5)
    ];

    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> workers = new();
    private Task? loop;
    private int pendingCount;

    public int PendingCount => Volatile.Read(ref pendingCount);
    public event EventHandler<int>? PendingCountChanged;

    public void Start()
    {
        if (loop is not null) return;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        loop = RunAsync(stopping.Token);
        Trigger();
    }

    public void Trigger()
    {
        if (signal.CurrentCount == 0)
            signal.Release();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var items = await SubmissionQueueStore.LoadAsync(ct);
                UpdatePendingCount(items.Count(x => !x.ReceiptReceived));
                var due = items.Where(x => !x.ReceiptReceived
                    && x.QueueStatus is not (SubmissionQueueStatus.Expired or SubmissionQueueStatus.FailedPermanent)
                    && (!x.NextRetryAtUtc.HasValue || x.NextRetryAtUtc <= DateTimeOffset.UtcNow)).ToList();
                await Task.WhenAll(due.Select(item => ProcessWithGateAsync(item, ct)));

                var delay = Task.Delay(TimeSpan.FromSeconds(5), ct);
                var triggered = signal.WaitAsync(ct);
                await Task.WhenAny(delay, triggered);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                FrontendLogger.Log(ex, "SubmissionRecovery.Loop");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task ProcessWithGateAsync(PendingSubmission item, CancellationToken ct)
    {
        var gate = workers.GetOrAdd(item.QueueId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) return;
        try { await ProcessAsync(item, ct); }
        finally { gate.Release(); }
    }

    private async Task ProcessAsync(PendingSubmission item, CancellationToken ct)
    {
        if (!authState.IsAuthenticated || !item.OwnerUserId.HasValue || authState.CurrentAccount?.UserId != item.OwnerUserId.Value)
        {
            await SaveStateAsync(item, SubmissionQueueStatus.NeedsLogin, "Cần đăng nhập đúng tài khoản đã tạo bài nộp.", false, ct);
            return;
        }

        if (!File.Exists(item.FilePath)
            || new FileInfo(item.FilePath).Length != item.SizeBytes
            || !string.Equals(await SubmissionQueueStore.HashFileAsync(item.FilePath, ct), item.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            await SaveStateAsync(item, SubmissionQueueStatus.FailedPermanent, "File spool bị thiếu hoặc không còn khớp SHA-256.", false, ct);
            return;
        }

        if (item.AccessMode == SessionAccessMode.PublicCloud)
        {
            await ProcessPublicCloudAsync(item, ct);
            return;
        }

        var current = item;
        if (current.QueueStatus == SubmissionQueueStatus.NeedsRejoin
            && sessionState.SessionId == current.SessionId
            && sessionState.ParticipantId == current.ParticipantId
            && !string.IsNullOrWhiteSpace(sessionState.AccessToken))
        {
            current = current with
            {
                ProtectedToken = SubmissionQueueStore.ProtectToken(sessionState.AccessToken),
                QueueStatus = SubmissionQueueStatus.Prepared,
                LastError = null,
                NextRetryAtUtc = DateTimeOffset.UtcNow
            };
            await SubmissionQueueStore.SaveAsync(current, ct);
        }
        try
        {
            var endpoint = await ResolveEndpointAsync(current, ct);
            var client = new BackendClient(endpoint);
            client.SetParticipantToken(SubmissionQueueStore.UnprotectToken(current.ProtectedToken));

            current = current with { Endpoint = endpoint, QueueStatus = SubmissionQueueStatus.Initializing, LastError = null };
            await SubmissionQueueStore.SaveAsync(current, ct);
            var init = ApiGuard.Require(await client.PostAsync<InitSubmissionRequest, InitSubmissionResponse>(
                "api/v1/submissions/init",
                new(current.SessionId, current.ParticipantId, current.IdempotencyKey,
                    [new InitSubmissionFileRequest("spool-1", current.FileName, current.SizeBytes, current.Sha256, "application/octet-stream")],
                    current.CreatedAtUtc), ct));
            var plan = init.FilePlans.Single();
            current = current with
            {
                SubmissionId = init.SubmissionId,
                ServerFileId = plan.FileId,
                ChunkSizeBytes = init.ChunkSizeBytes,
                MissingChunks = plan.MissingChunks,
                QueueStatus = SubmissionQueueStatus.Uploading
            };
            await SubmissionQueueStore.SaveAsync(current, ct);

            await using var stream = new FileStream(current.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, init.ChunkSizeBytes, true);
            var buffer = new byte[init.ChunkSizeBytes];
            var missing = plan.MissingChunks.ToList();
            foreach (var index in plan.MissingChunks)
            {
                stream.Position = (long)index * init.ChunkSizeBytes;
                var expected = (int)Math.Min(init.ChunkSizeBytes, current.SizeBytes - stream.Position);
                var read = 0;
                while (read < expected)
                {
                    var count = await stream.ReadAsync(buffer.AsMemory(read, expected - read), ct);
                    if (count == 0) throw new EndOfStreamException("File spool kết thúc trước chunk dự kiến.");
                    read += count;
                }
                await using var chunk = new MemoryStream(buffer, 0, read, false, true);
                ApiGuard.Require(await client.UploadChunkAsync($"api/v1/submissions/{init.SubmissionId}/files/{plan.FileId}/chunks/{index}", chunk, read, null, ct));
                missing.Remove(index);
                current = current with { MissingChunks = missing.ToList() };
                await SubmissionQueueStore.SaveAsync(current, ct);
            }

            current = current with { QueueStatus = SubmissionQueueStatus.Finalizing, FinalizeRequested = true };
            await SubmissionQueueStore.SaveAsync(current, ct);
            _ = ApiGuard.Require(await client.PostAsync<FinalizeSubmissionRequest, FinalizeSubmissionResponse>($"api/v1/submissions/{init.SubmissionId}/finalize", new(null), ct));
            current = current with { QueueStatus = SubmissionQueueStatus.AwaitingReceipt };
            await SubmissionQueueStore.SaveAsync(current, ct);
            var receipt = ApiGuard.Require(await client.GetAsync<ReceiptDto>($"api/v1/submissions/{init.SubmissionId}/receipt", ct));
            await SubmissionQueueStore.StoreReceiptAsync(receipt, ct);
            current = current with { QueueStatus = SubmissionQueueStatus.Completed, ReceiptReceived = true, CompletedAtUtc = DateTimeOffset.UtcNow, LastError = null };
            await SubmissionQueueStore.SaveAsync(current, ct);
            UpdateCurrentSession(current, receipt);
            await SubmissionQueueStore.RemoveCompletedAsync(current.QueueId, ct);
            UpdatePendingCount(Math.Max(0, PendingCount - 1));
        }
        catch (BackendApiException ex) when (ex.ApiCode is ErrorCodes.TokenExpired or ErrorCodes.ParticipantTokenRequired or ErrorCodes.ParticipantAccountMismatch)
        {
            await SaveStateAsync(current, SubmissionQueueStatus.NeedsRejoin, "Bài đã được lưu an toàn trên máy và đang chờ xác nhận lại từ phòng thi.", false, ct);
        }
        catch (BackendApiException ex) when (ex.ApiCode is ErrorCodes.SubmissionArchiveRequired or ErrorCodes.SubmissionTooLarge or ErrorCodes.SubmissionFileCountInvalid)
        {
            await SaveStateAsync(current, SubmissionQueueStatus.FailedPermanent, ex.Message, false, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or BackendApiException)
        {
            await SaveStateAsync(current, SubmissionQueueStatus.WaitingForConnection, ex.Message, true, ct);
        }
    }

    private async Task ProcessPublicCloudAsync(PendingSubmission item, CancellationToken ct)
    {
        var current = item;
        if (!AppServices.PublicCloud.Configured || !AppServices.PublicCloud.Authenticated)
        {
            await SaveStateAsync(current, SubmissionQueueStatus.NeedsLogin,
                "Bài PublicCloud đã được spool an toàn; hãy đăng nhập lại Supabase để gửi tiếp.", false, ct);
            return;
        }
        try
        {
            current = current with { QueueStatus = SubmissionQueueStatus.Initializing, LastError = null };
            await SubmissionQueueStore.SaveAsync(current, ct);
            var plan = await AppServices.PublicCloud.InitSubmissionAsync(
                current.SessionId, current.IdempotencyKey, current.FileName,
                current.SizeBytes, current.Sha256, ct);
            current = current with
            {
                SubmissionId = plan.SubmissionId,
                ServerFileId = plan.FileId,
                QueueStatus = SubmissionQueueStatus.Uploading
            };
            await SubmissionQueueStore.SaveAsync(current, ct);
            await AppServices.PublicCloud.UploadSubmissionArchiveAsync(plan, current.FilePath, ct);
            current = current with { QueueStatus = SubmissionQueueStatus.Finalizing, FinalizeRequested = true };
            await SubmissionQueueStore.SaveAsync(current, ct);
            var receipt = await AppServices.PublicCloud.VerifyAndFinalizeSubmissionAsync(plan, current.IdempotencyKey, ct);
            await SubmissionQueueStore.StoreReceiptAsync(receipt, ct);
            current = current with
            {
                QueueStatus = SubmissionQueueStatus.Completed,
                ReceiptReceived = true,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                LastError = null
            };
            await SubmissionQueueStore.SaveAsync(current, ct);
            UpdateCurrentSession(current, receipt);
            await SubmissionQueueStore.RemoveCompletedAsync(current.QueueId, ct);
            UpdatePendingCount(Math.Max(0, PendingCount - 1));
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.BadRequest
            or System.Net.HttpStatusCode.UnprocessableEntity)
        {
            await SaveStateAsync(current, SubmissionQueueStatus.FailedPermanent, ex.Message, false, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            await SaveStateAsync(current, SubmissionQueueStatus.WaitingForConnection, ex.Message, true, ct);
        }
    }

    private async Task<string> ResolveEndpointAsync(PendingSubmission item, CancellationToken ct)
    {
        if (item.AccessMode != SessionAccessMode.LanOnly) return item.Endpoint;
        var rooms = await discovery.DiscoverOpenSessionsAsync(TimeSpan.FromSeconds(2), ct);
        var room = rooms.FirstOrDefault(x => x.SessionId == item.SessionId)
            ?? rooms.FirstOrDefault(x => !string.IsNullOrWhiteSpace(item.RoomCode) && x.RoomCode.Equals(item.RoomCode, StringComparison.OrdinalIgnoreCase));
        return room?.BaseAddress ?? item.Endpoint;
    }

    private static async Task SaveStateAsync(PendingSubmission item, SubmissionQueueStatus status, string error, bool retry, CancellationToken ct)
    {
        var retryCount = retry ? item.RetryCount + 1 : item.RetryCount;
        var delay = retry ? RetrySchedule[Math.Min(retryCount, RetrySchedule.Length) - 1] : TimeSpan.Zero;
        await SubmissionQueueStore.SaveAsync(item with
        {
            QueueStatus = status,
            RetryCount = retryCount,
            NextRetryAtUtc = retry ? DateTimeOffset.UtcNow.Add(delay) : null,
            LastError = error
        }, ct);
    }

    private void UpdateCurrentSession(PendingSubmission item, ReceiptDto receipt)
    {
        if (sessionState.SessionId != item.SessionId || sessionState.ParticipantId != item.ParticipantId) return;
        void Apply()
        {
            sessionState.LastSubmissionId = receipt.SubmissionId;
            sessionState.LastReceipt = receipt;
        }
        if (Application.Current?.Dispatcher?.CheckAccess() == false)
            Application.Current.Dispatcher.Invoke(Apply);
        else
            Apply();
    }

    private void UpdatePendingCount(int value)
    {
        if (Interlocked.Exchange(ref pendingCount, value) != value)
            PendingCountChanged?.Invoke(this, value);
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) Trigger();
    }

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        stopping.Cancel();
        signal.Dispose();
        stopping.Dispose();
        foreach (var gate in workers.Values) gate.Dispose();
    }
}
