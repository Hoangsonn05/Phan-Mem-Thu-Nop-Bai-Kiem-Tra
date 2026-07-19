using System.Security.Cryptography;
using System.Text;
using ExamTransfer.Application;
using ExamTransfer.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace ExamTransfer.Infrastructure.Security;

public sealed class ReceiptSigner : IReceiptSigner
{
    private readonly byte[] _key;

    public ReceiptSigner(IOptions<ExamTransferOptions> options)
    {
        var configured = options.Value.Security.ReceiptSigningKey;
        if (string.IsNullOrWhiteSpace(configured))
        {
            var seed = $"{Environment.MachineName}|ExamTransfer|receipt-v1";
            configured = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
        }
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
    }

    public (string ReceiptCode, string Signature) Create(Guid submissionId, DateTimeOffset receivedAtUtc, IReadOnlyList<FileDescriptorDto> files)
    {
        var code = $"ET-{receivedAtUtc:yyyyMMddHHmmss}-{submissionId.ToString("N")[..10].ToUpperInvariant()}";
        var canonical = Canonical(submissionId, receivedAtUtc, files);
        var signature = Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return (code, signature);
    }

    public bool Verify(Guid submissionId, DateTimeOffset receivedAtUtc, IReadOnlyList<FileDescriptorDto> files, string signature)
    {
        var expected = Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(Canonical(submissionId, receivedAtUtc, files)))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature.ToLowerInvariant()));
    }

    private static string Canonical(Guid id, DateTimeOffset time, IReadOnlyList<FileDescriptorDto> files)
    {
        var filePart = string.Join('|', files.OrderBy(x => x.Id).Select(x => $"{x.Id:N}:{x.SizeBytes}:{x.Sha256.ToLowerInvariant()}"));
        return $"{id:N}|{time.ToUniversalTime():O}|{filePart}";
    }
}
