namespace ExamTransfer.Infrastructure.Storage;

public static class ArchiveSignatureValidator
{
    private static readonly byte[][] ZipSignatures =
    [
        [0x50, 0x4B, 0x03, 0x04],
        [0x50, 0x4B, 0x05, 0x06],
        [0x50, 0x4B, 0x07, 0x08]
    ];
    private static readonly byte[] RarSignature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07];
    private static readonly byte[] SevenZipSignature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

    public static async Task<bool> MatchesExtensionAsync(string path, string fileName, CancellationToken cancellationToken = default)
    {
        var header = new byte[8];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, header.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var read = await stream.ReadAsync(header.AsMemory(), cancellationToken);
        var extension = Path.GetExtension(Path.GetFileName(fileName));
        return extension.ToLowerInvariant() switch
        {
            ".zip" => ZipSignatures.Any(signature => StartsWith(header, read, signature)),
            ".rar" => StartsWith(header, read, RarSignature),
            ".7z" => StartsWith(header, read, SevenZipSignature),
            _ => false
        };
    }

    private static bool StartsWith(byte[] buffer, int length, byte[] signature) =>
        length >= signature.Length && buffer.AsSpan(0, signature.Length).SequenceEqual(signature);
}
