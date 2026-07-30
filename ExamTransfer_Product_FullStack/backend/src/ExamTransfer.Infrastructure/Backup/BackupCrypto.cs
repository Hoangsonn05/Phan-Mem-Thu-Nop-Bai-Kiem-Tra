using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ExamTransfer.Application;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Infrastructure.Backup;

/// <summary>
/// Streaming, authenticated backup encryption. The passphrase is supplied only
/// through a trusted backend environment variable and is never stored in the
/// desktop source, SQLite database, backup manifest, or Supabase metadata.
/// </summary>
public static class BackupCrypto
{
    public const string KeyEnvironmentVariable = "EXAMTRANSFER_BACKUP_KEY";

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ETBACK01");
    private const int SaltSize = 16;
    private const int NoncePrefixSize = 8;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int DefaultChunkSize = 1024 * 1024;
    private const int Pbkdf2Iterations = 200_000;

    public static string GetRequiredPassphrase()
    {
        var value = Environment.GetEnvironmentVariable(KeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value) || value.Length < 12)
        {
            throw new ApiException(
                ErrorCodes.ValidationFailed,
                $"Để mã hóa hoặc khôi phục backup, hãy cấu hình biến môi trường {KeyEnvironmentVariable} với ít nhất 12 ký tự.",
                409);
        }

        return value;
    }

    public static bool IsEncrypted(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < Magic.Length)
            return false;

        Span<byte> header = stackalloc byte[Magic.Length];
        return stream.Read(header) == Magic.Length && header.SequenceEqual(Magic);
    }

    public static async Task EncryptFileAsync(
        string sourcePath,
        string destinationPath,
        string passphrase,
        CancellationToken cancellationToken)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var noncePrefix = RandomNumberGenerator.GetBytes(NoncePrefixSize);
        var key = DeriveKey(passphrase, salt);
        var temporaryPath = destinationPath + ".tmp";

        try
        {
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                DefaultChunkSize,
                useAsync: true);
            await using var destination = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                DefaultChunkSize,
                useAsync: true);

            await destination.WriteAsync(Magic, cancellationToken);
            await destination.WriteAsync(salt, cancellationToken);
            await destination.WriteAsync(noncePrefix, cancellationToken);
            await WriteInt32Async(destination, DefaultChunkSize, cancellationToken);

            using var aes = new AesGcm(key, TagSize);
            var plainBuffer = new byte[DefaultChunkSize];
            var counter = 0;

            while (true)
            {
                var read = await source.ReadAsync(plainBuffer.AsMemory(), cancellationToken);
                if (read == 0)
                    break;

                var cipher = new byte[read];
                var tag = new byte[TagSize];
                var nonce = CreateNonce(noncePrefix, counter);
                var aad = CreateAdditionalData(counter, read);
                aes.Encrypt(nonce, plainBuffer.AsSpan(0, read), cipher, tag, aad);

                await WriteInt32Async(destination, read, cancellationToken);
                await destination.WriteAsync(cipher, cancellationToken);
                await destination.WriteAsync(tag, cancellationToken);
                counter = checked(counter + 1);
            }

            await WriteInt32Async(destination, 0, cancellationToken);
            await destination.FlushAsync(cancellationToken);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public static async Task DecryptFileAsync(
        string sourcePath,
        string destinationPath,
        string passphrase,
        CancellationToken cancellationToken)
    {
        var temporaryPath = destinationPath + ".tmp";
        byte[]? key = null;

        try
        {
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                DefaultChunkSize,
                useAsync: true);

            var magic = new byte[Magic.Length];
            await ReadExactlyAsync(source, magic, cancellationToken);
            if (!magic.SequenceEqual(Magic))
                throw new InvalidDataException("File không phải backup ExamTransfer đã mã hóa.");

            var salt = new byte[SaltSize];
            var noncePrefix = new byte[NoncePrefixSize];
            await ReadExactlyAsync(source, salt, cancellationToken);
            await ReadExactlyAsync(source, noncePrefix, cancellationToken);
            var chunkSize = await ReadInt32Async(source, cancellationToken);
            if (chunkSize is < 64 * 1024 or > 16 * 1024 * 1024)
                throw new InvalidDataException("Kích thước chunk backup mã hóa không hợp lệ.");

            key = DeriveKey(passphrase, salt);
            using var aes = new AesGcm(key, TagSize);
            await using var destination = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                chunkSize,
                useAsync: true);

            var counter = 0;
            while (true)
            {
                var length = await ReadInt32Async(source, cancellationToken);
                if (length == 0)
                    break;
                if (length < 0 || length > chunkSize)
                    throw new InvalidDataException("Bản ghi backup mã hóa có kích thước không hợp lệ.");

                var cipher = new byte[length];
                var tag = new byte[TagSize];
                var plain = new byte[length];
                await ReadExactlyAsync(source, cipher, cancellationToken);
                await ReadExactlyAsync(source, tag, cancellationToken);

                var nonce = CreateNonce(noncePrefix, counter);
                var aad = CreateAdditionalData(counter, length);
                aes.Decrypt(nonce, cipher, tag, plain, aad);
                await destination.WriteAsync(plain, cancellationToken);
                counter = checked(counter + 1);
            }

            await destination.FlushAsync(cancellationToken);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        catch (CryptographicException ex)
        {
            throw new ApiException(
                ErrorCodes.ValidationFailed,
                "Không thể giải mã backup. Khóa không đúng hoặc file đã bị thay đổi.",
                409,
                details: ex.Message);
        }
        finally
        {
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            passphrase,
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeySize);

    private static byte[] CreateNonce(byte[] prefix, int counter)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteInt32BigEndian(nonce.AsSpan(8), counter);
        return nonce;
    }

    private static byte[] CreateAdditionalData(int counter, int length)
    {
        var aad = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(0, 4), counter);
        BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(4, 4), length);
        return aad;
    }

    private static async Task WriteInt32Async(Stream stream, int value, CancellationToken cancellationToken)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        await stream.WriteAsync(buffer, cancellationToken);
    }

    private static async Task<int> ReadInt32Async(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4];
        await ReadExactlyAsync(stream, buffer, cancellationToken);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("File backup bị cắt ngắn.");
            offset += read;
        }
    }
}
