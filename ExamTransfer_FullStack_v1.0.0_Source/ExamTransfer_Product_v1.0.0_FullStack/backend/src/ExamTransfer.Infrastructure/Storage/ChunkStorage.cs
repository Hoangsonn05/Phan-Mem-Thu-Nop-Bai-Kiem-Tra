using System.Security.Cryptography;
using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Infrastructure.Storage;

public sealed class ChunkStorage : IChunkStorage
{
    public async Task WriteChunkAsync(string transferRoot, int index, Stream content, long maxBytes, string? expectedSha256, CancellationToken cancellationToken)
    {
        if (index < 0) throw new ApiException(ErrorCodes.ChunkMismatch, "Chỉ số chunk không hợp lệ.");
        Directory.CreateDirectory(transferRoot);
        var target = Path.Combine(transferRoot, $"{index:D8}.chunk");
        var temp = target + ".tmp";

        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
        {
            var buffer = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                total += read;
                if (total > maxBytes)
                    throw new ApiException(ErrorCodes.ChunkMismatch, "Chunk vượt quá kích thước cho phép.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await output.FlushAsync(cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            var actual = await ComputeSha256Async(temp, cancellationToken);
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temp);
                throw new ApiException(ErrorCodes.HashMismatch, "Hash của chunk không khớp.");
            }
        }

        if (File.Exists(target))
        {
            var existingHash = await ComputeSha256Async(target, cancellationToken);
            var incomingHash = await ComputeSha256Async(temp, cancellationToken);
            if (!existingHash.Equals(incomingHash, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temp);
                throw new ApiException(ErrorCodes.ChunkMismatch, "Chunk đã tồn tại nhưng nội dung khác.", 409);
            }
            File.Delete(temp);
            return;
        }

        File.Move(temp, target);
    }

    public async Task<string> AssembleAndVerifyAsync(string transferRoot, int totalChunks, long expectedSize, string expectedSha256, string outputPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var temp = outputPath + ".assembling";
        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
        {
            for (var i = 0; i < totalChunks; i++)
            {
                var chunk = Path.Combine(transferRoot, $"{i:D8}.chunk");
                if (!File.Exists(chunk))
                    throw new ApiException(ErrorCodes.ChunkMismatch, $"Thiếu chunk {i}.");
                await using var input = new FileStream(chunk, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
                await input.CopyToAsync(output, cancellationToken);
            }
            await output.FlushAsync(cancellationToken);
        }

        var info = new FileInfo(temp);
        if (info.Length != expectedSize)
        {
            File.Delete(temp);
            throw new ApiException(ErrorCodes.ChunkMismatch, $"Kích thước file không khớp. Dự kiến {expectedSize}, thực tế {info.Length}.");
        }

        var actualHash = await ComputeSha256Async(temp, cancellationToken);
        if (!actualHash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temp);
            throw new ApiException(ErrorCodes.HashMismatch, "SHA-256 của file hoàn chỉnh không khớp.");
        }

        if (File.Exists(outputPath)) File.Delete(outputPath);
        File.Move(temp, outputPath);
        return actualHash;
    }

    public IReadOnlyList<int> ReadReceivedChunks(string json)
    {
        try { return JsonSerializer.Deserialize<List<int>>(json) ?? []; }
        catch { return []; }
    }

    public string WriteReceivedChunks(IReadOnlyCollection<int> chunks) =>
        JsonSerializer.Serialize(chunks.OrderBy(x => x));

    public async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
