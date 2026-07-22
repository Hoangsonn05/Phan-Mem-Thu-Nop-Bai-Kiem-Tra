using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ExamTransfer.Desktop.Infrastructure;

public sealed record PendingSubmission(
    Guid QueueId, string Endpoint, Guid SessionId, Guid ParticipantId, string ProtectedToken,
    string FilePath, string FileName, long SizeBytes, string Sha256, string IdempotencyKey,
    Guid? SubmissionId, Guid? ServerFileId, int ChunkSizeBytes, IReadOnlyList<int> MissingChunks,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public static class SubmissionQueueStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExamTransfer", "submission-queue");
    private static string QueuePath => Path.Combine(Root, "queue.json");

    public static string ProtectToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return string.Empty;
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
    }

    public static async Task<IReadOnlyList<PendingSubmission>> LoadAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try { return await ReadCoreAsync(ct); }
        finally { Gate.Release(); }
    }

    public static async Task SaveAsync(PendingSubmission item, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var items = (await ReadCoreAsync(ct)).ToList();
            var index = items.FindIndex(x => x.QueueId == item.QueueId);
            if (index >= 0) items[index] = item with { UpdatedAtUtc = DateTimeOffset.UtcNow }; else items.Add(item);
            await WriteCoreAsync(items, ct);
        }
        finally { Gate.Release(); }
    }

    public static async Task RemoveAsync(Guid queueId, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try { await WriteCoreAsync((await ReadCoreAsync(ct)).Where(x => x.QueueId != queueId).ToList(), ct); }
        finally { Gate.Release(); }
    }

    private static async Task<IReadOnlyList<PendingSubmission>> ReadCoreAsync(CancellationToken ct)
    {
        if (!File.Exists(QueuePath)) return [];
        try
        {
            await using var stream = new FileStream(QueuePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            return await JsonSerializer.DeserializeAsync<List<PendingSubmission>>(stream, Json, ct) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static async Task WriteCoreAsync(IReadOnlyList<PendingSubmission> items, CancellationToken ct)
    {
        Directory.CreateDirectory(Root); var temporary = QueuePath + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await JsonSerializer.SerializeAsync(stream, items, Json, ct);
        File.Move(temporary, QueuePath, true);
    }
}
