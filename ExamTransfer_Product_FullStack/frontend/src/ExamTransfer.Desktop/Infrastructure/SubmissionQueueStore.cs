using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Infrastructure;

public enum SubmissionQueueStatus
{
    Prepared,
    WaitingForConnection,
    Initializing,
    Uploading,
    Finalizing,
    AwaitingReceipt,
    Completed,
    NeedsLogin,
    NeedsRejoin,
    Expired,
    FailedPermanent
}

public sealed record PendingSubmission(
    Guid QueueId,
    string Endpoint,
    Guid SessionId,
    Guid ParticipantId,
    string ProtectedToken,
    string FilePath,
    string FileName,
    long SizeBytes,
    string Sha256,
    string IdempotencyKey,
    Guid? SubmissionId,
    Guid? ServerFileId,
    int ChunkSizeBytes,
    IReadOnlyList<int> MissingChunks,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    Guid? OwnerUserId = null,
    string? StudentCode = null,
    Guid? ClassId = null,
    string? RoomCode = null,
    SessionAccessMode AccessMode = SessionAccessMode.LanOnly,
    string? ServerId = null,
    string? CloudDestination = null,
    SubmissionQueueStatus QueueStatus = SubmissionQueueStatus.Prepared,
    int RetryCount = 0,
    DateTimeOffset? NextRetryAtUtc = null,
    string? LastError = null,
    bool FinalizeRequested = false,
    bool ReceiptReceived = false,
    DateTimeOffset? CompletedAtUtc = null,
    string? OriginalSourcePath = null);

public sealed record SubmissionProgressSnapshot(
    Guid QueueId,
    SubmissionQueueStatus Status,
    double ProgressPercent,
    Guid? SubmissionId,
    bool ReceiptReceived,
    string? LastError,
    bool IsCompleted,
    bool IsTerminal,
    int CompletedChunks,
    int TotalChunks);

public enum SubmissionPreparationOutcome
{
    Created,
    ExistingActive
}

public sealed record SubmissionPreparationResult(
    PendingSubmission Submission,
    SubmissionPreparationOutcome Outcome)
{
    public bool Created => Outcome == SubmissionPreparationOutcome.Created;
}

public static class SubmissionQueueStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly ConcurrentDictionary<SubmissionBusinessKey, BusinessKeyLock> BusinessKeyLocks = new();
    private static readonly AsyncLocal<string?> StorageRootOverride = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static string LocalDataRoot => StorageRootOverride.Value ?? AppProfile.LocalDataRoot;
    private static string Root => Path.Combine(LocalDataRoot, "submission-queue");
    private static string SpoolRoot => Path.Combine(LocalDataRoot, "submission-spool");
    private static string ReceiptRoot => Path.Combine(LocalDataRoot, "receipts");
    private static string QueuePath => Path.Combine(Root, "queue.json");

    internal static int ActiveBusinessKeyLockCount => BusinessKeyLocks.Count;

    internal static IDisposable UseStorageRootForTests(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var previous = StorageRootOverride.Value;
        StorageRootOverride.Value = Path.GetFullPath(root);
        return new StorageRootScope(previous);
    }

    public static string ProtectToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return string.Empty;
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
    }

    public static string? UnprotectToken(string? protectedToken)
    {
        if (string.IsNullOrWhiteSpace(protectedToken)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedToken), null, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public static async Task<PendingSubmission> PrepareAsync(
        string sourcePath,
        string endpoint,
        Guid ownerUserId,
        string studentCode,
        Guid sessionId,
        Guid participantId,
        string? roomCode,
        SessionAccessMode accessMode,
        string? serverId,
        string? token,
        CancellationToken ct) =>
        (await PrepareOrGetActiveAsync(
            sourcePath,
            endpoint,
            ownerUserId,
            studentCode,
            sessionId,
            participantId,
            roomCode,
            accessMode,
            serverId,
            token,
            ct)).Submission;

    public static async Task<SubmissionPreparationResult> PrepareOrGetActiveAsync(
        string sourcePath,
        string endpoint,
        Guid ownerUserId,
        string studentCode,
        Guid sessionId,
        Guid participantId,
        string? roomCode,
        SessionAccessMode accessMode,
        string? serverId,
        string? token,
        CancellationToken ct)
    {
        var businessKey = new SubmissionBusinessKey(sessionId, participantId);
        using var keyLease = await AcquireBusinessKeyAsync(businessKey, ct);
        var existing = FindActiveQueue(await LoadAsync(ct), sessionId, participantId);
        if (existing is not null)
            return new(existing, SubmissionPreparationOutcome.ExistingActive);

        var source = new FileInfo(Path.GetFullPath(sourcePath));
        if (!source.Exists) throw new FileNotFoundException("Không tìm thấy file bài làm.", source.FullName);
        if (!StudentSubmissionPolicy.IsAllowedExtension(source.Name))
            throw new InvalidDataException("Bài làm phải được nén thành một file .zip, .rar hoặc .7z trước khi nộp.");
        if (source.Length <= 0 || source.Length > StudentSubmissionPolicy.MaxBytes)
            throw new InvalidDataException("File bài làm vượt quá 10 MB. Hãy xóa dữ liệu không cần thiết hoặc giảm dung lượng rồi nén lại.");
        if (!await MatchesArchiveSignatureAsync(source.FullName, source.Extension, ct))
            throw new InvalidDataException("Nội dung file không đúng định dạng nén theo phần mở rộng. Hãy tạo lại file ZIP/RAR/7Z rồi thử lại.");

        var queueId = Guid.NewGuid();
        var queueDirectory = Path.Combine(SpoolRoot, queueId.ToString("N"));
        var spoolPath = Path.Combine(queueDirectory, Path.GetFileName(source.Name));
        Directory.CreateDirectory(queueDirectory);
        try
        {
            await using (var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(spoolPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, ct);
                await output.FlushAsync(ct);
                output.Flush(true);
            }

            var sha256 = await HashFileAsync(spoolPath, ct);
            var now = DateTimeOffset.UtcNow;
            var item = new PendingSubmission(
                queueId, endpoint, sessionId, participantId, ProtectToken(token), spoolPath, source.Name,
                source.Length, sha256, Guid.NewGuid().ToString("N"), null, null, 0, [], now, now,
                ownerUserId, studentCode, null, roomCode, accessMode, serverId, null,
                SubmissionQueueStatus.Prepared, 0, now, null, false, false, null, source.FullName);
            await SaveAsync(item, ct);
            return new(item, SubmissionPreparationOutcome.Created);
        }
        catch
        {
            TryDeleteSpoolDirectory(queueDirectory);
            throw;
        }
    }

    public static bool IsActiveQueueStatus(SubmissionQueueStatus status) =>
        status is SubmissionQueueStatus.Prepared
            or SubmissionQueueStatus.WaitingForConnection
            or SubmissionQueueStatus.Initializing
            or SubmissionQueueStatus.Uploading
            or SubmissionQueueStatus.Finalizing
            or SubmissionQueueStatus.AwaitingReceipt
            or SubmissionQueueStatus.Completed
            or SubmissionQueueStatus.NeedsLogin
            or SubmissionQueueStatus.NeedsRejoin;

    public static bool IsActiveQueue(PendingSubmission item) =>
        !item.ReceiptReceived && IsActiveQueueStatus(item.QueueStatus);

    public static PendingSubmission? FindActiveQueue(
        IEnumerable<PendingSubmission> items,
        Guid sessionId,
        Guid participantId) =>
        items
            .Where(item => item.SessionId == sessionId
                && item.ParticipantId == participantId
                && IsActiveQueue(item))
            .OrderBy(item => item.CreatedAtUtc)
            .FirstOrDefault();

    public static async Task<IReadOnlyList<PendingSubmission>> LoadAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try { return await ReadCoreAsync(ct); }
        finally { Gate.Release(); }
    }

    public static SubmissionProgressSnapshot CreateProgressSnapshot(PendingSubmission item)
    {
        var totalChunks = item.ChunkSizeBytes > 0 && item.SizeBytes > 0
            ? checked((int)Math.Ceiling(item.SizeBytes / (double)item.ChunkSizeBytes))
            : 0;
        var missingChunks = totalChunks == 0
            ? 0
            : Math.Clamp(item.MissingChunks.Distinct().Count(), 0, totalChunks);
        var completedChunks = totalChunks - missingChunks;
        var uploadProgress = totalChunks > 0
            ? 15d + (70d * completedChunks / totalChunks)
            : 15d;
        var lastKnownProgress = item.FinalizeRequested
            ? 90d
            : item.SubmissionId.HasValue
                ? uploadProgress
                : 10d;
        var completed = item.ReceiptReceived;
        var terminal = completed
            || item.QueueStatus is SubmissionQueueStatus.Expired
                or SubmissionQueueStatus.FailedPermanent;
        var progress = completed
            ? 100d
            : item.QueueStatus switch
            {
                SubmissionQueueStatus.Prepared => 10d,
                SubmissionQueueStatus.Initializing => 15d,
                SubmissionQueueStatus.Uploading => uploadProgress,
                SubmissionQueueStatus.Finalizing => 90d,
                SubmissionQueueStatus.AwaitingReceipt
                    or SubmissionQueueStatus.Completed => 95d,
                SubmissionQueueStatus.WaitingForConnection
                    or SubmissionQueueStatus.NeedsLogin
                    or SubmissionQueueStatus.NeedsRejoin
                    or SubmissionQueueStatus.Expired
                    or SubmissionQueueStatus.FailedPermanent => lastKnownProgress,
                _ => lastKnownProgress
            };

        return new SubmissionProgressSnapshot(
            item.QueueId,
            item.QueueStatus,
            progress,
            item.SubmissionId,
            item.ReceiptReceived,
            item.LastError,
            completed,
            terminal,
            completedChunks,
            totalChunks);
    }

    public static async Task SaveAsync(PendingSubmission item, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var items = (await ReadCoreAsync(ct)).ToList();
            var normalized = item with { UpdatedAtUtc = DateTimeOffset.UtcNow };
            var index = items.FindIndex(x => x.QueueId == item.QueueId);
            if (index >= 0) items[index] = normalized; else items.Add(normalized);
            await WriteCoreAsync(items, ct);
        }
        finally { Gate.Release(); }
    }

    public static async Task StoreReceiptAsync(ReceiptDto receipt, CancellationToken ct)
    {
        Directory.CreateDirectory(ReceiptRoot);
        var path = Path.Combine(ReceiptRoot, $"receipt-{receipt.ReceiptCode}.json");
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, receipt, Json, ct);
            await stream.FlushAsync(ct);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    public static async Task RemoveCompletedAsync(Guid queueId, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var items = (await ReadCoreAsync(ct)).ToList();
            var item = items.FirstOrDefault(x => x.QueueId == queueId);
            if (item is null || !item.ReceiptReceived) return;
            await WriteCoreAsync(items.Where(x => x.QueueId != queueId).ToList(), ct);
            TryDeleteSpoolDirectory(Path.GetDirectoryName(item.FilePath));
        }
        finally { Gate.Release(); }
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    private static async Task<IReadOnlyList<PendingSubmission>> ReadCoreAsync(CancellationToken ct)
    {
        if (!File.Exists(QueuePath)) return [];
        try
        {
            await using var stream = new FileStream(QueuePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            return await JsonSerializer.DeserializeAsync<List<PendingSubmission>>(stream, Json, ct) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Hàng đợi bài nộp bị hỏng; dữ liệu gốc được giữ lại để khôi phục thủ công.", ex);
        }
    }

    private static async Task WriteCoreAsync(IReadOnlyList<PendingSubmission> items, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        var temporary = QueuePath + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, items, Json, ct);
            await stream.FlushAsync(ct);
            stream.Flush(true);
        }
        File.Move(temporary, QueuePath, true);
    }

    private static async Task<bool> MatchesArchiveSignatureAsync(string path, string extension, CancellationToken ct)
    {
        var header = new byte[8];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, header.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var read = await stream.ReadAsync(header.AsMemory(), ct);
        bool StartsWith(params byte[] signature) => read >= signature.Length && header.AsSpan(0, signature.Length).SequenceEqual(signature);
        return extension.ToLowerInvariant() switch
        {
            ".zip" => StartsWith(0x50, 0x4B, 0x03, 0x04) || StartsWith(0x50, 0x4B, 0x05, 0x06) || StartsWith(0x50, 0x4B, 0x07, 0x08),
            ".rar" => StartsWith(0x52, 0x61, 0x72, 0x21, 0x1A, 0x07),
            ".7z" => StartsWith(0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C),
            _ => false
        };
    }

    private static void TryDeleteSpoolDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        var fullRoot = Path.GetFullPath(SpoolRoot) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return;
        try { Directory.Delete(path, true); } catch (IOException) { }
    }

    private static async Task<BusinessKeyLease> AcquireBusinessKeyAsync(
        SubmissionBusinessKey key,
        CancellationToken ct)
    {
        while (true)
        {
            var entry = BusinessKeyLocks.GetOrAdd(key, static _ => new BusinessKeyLock());
            lock (entry.SyncRoot)
            {
                if (entry.Retired)
                    continue;
                entry.ReferenceCount++;
            }

            try
            {
                await entry.Semaphore.WaitAsync(ct);
                return new BusinessKeyLease(key, entry);
            }
            catch
            {
                ReleaseBusinessKey(key, entry, acquired: false);
                throw;
            }
        }
    }

    private static void ReleaseBusinessKey(
        SubmissionBusinessKey key,
        BusinessKeyLock entry,
        bool acquired)
    {
        if (acquired)
            entry.Semaphore.Release();

        var dispose = false;
        lock (entry.SyncRoot)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0)
            {
                entry.Retired = true;
                dispose = BusinessKeyLocks.TryRemove(
                    new KeyValuePair<SubmissionBusinessKey, BusinessKeyLock>(key, entry));
            }
        }

        if (dispose)
            entry.Semaphore.Dispose();
    }

    private readonly record struct SubmissionBusinessKey(
        Guid SessionId,
        Guid ParticipantId);

    private sealed class BusinessKeyLock
    {
        public object SyncRoot { get; } = new();
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
        public bool Retired { get; set; }
    }

    private sealed class BusinessKeyLease(
        SubmissionBusinessKey key,
        BusinessKeyLock entry) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                ReleaseBusinessKey(key, entry, acquired: true);
        }
    }

    private sealed class StorageRootScope(string? previous) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                StorageRootOverride.Value = previous;
        }
    }
}
