using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml.Linq;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using SharpCompress.Archives;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace ExamTransfer.Infrastructure.Services;

public sealed class SubmissionPreviewService(
    AppDbContext db,
    IStoragePaths paths) : ISubmissionPreviewService
{
    private const int MaxEntries = 500;
    private const long MaxExpandedBytes = 200L * 1024 * 1024;
    private const long MaxSourceBytes = 2L * 1024 * 1024;
    private const int MaxReturnedCharacters = 200_000;
    private const double MaxCompressionRatio = 100d;
    private static readonly HashSet<string> ArchiveExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z" };
    private static readonly HashSet<string> TextExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".txt", ".py", ".java", ".html", ".htm" };

    public async Task<SubmissionPreviewManifestDto> GetManifestAsync(
        Guid submissionId,
        Guid fileId,
        string? organizationId,
        CancellationToken cancellationToken)
    {
        var file = await AuthorizedFileAsync(submissionId, fileId, organizationId, cancellationToken);
        var path = SafePhysicalPath(file.RelativePath);
        var extension = Path.GetExtension(file.OriginalName);
        if (!ArchiveExtensions.Contains(extension))
        {
            return new(
                submissionId,
                fileId,
                file.OriginalName,
                false,
                [new(file.OriginalName, file.OriginalName, file.SizeBytes, file.SizeBytes, IsPreviewSupported(file.OriginalName), UnsupportedReason(file.OriginalName))]);
        }
        ValidateArchiveSignature(path, extension);
        using var archive = ArchiveFactory.OpenArchive(path);
        var entries = new List<SubmissionPreviewEntryDto>();
        long expanded = 0;
        foreach (var entry in archive.Entries.Where(x => !x.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entries.Count >= MaxEntries)
                throw UnsafeArchive("Archive vượt giới hạn 500 entry.");
            var key = entry.Key ?? throw UnsafeArchive("Archive chứa entry không có tên.");
            ValidateEntryPath(key);
            if (entry.IsEncrypted)
                throw UnsafeArchive("Archive được mã hóa hoặc có mật khẩu nên không thể preview.");
            expanded = checked(expanded + entry.Size);
            if (expanded > MaxExpandedBytes)
                throw UnsafeArchive("Archive vượt giới hạn 200 MB sau giải nén.");
            if (entry.CompressedSize > 0
                && entry.Size / (double)entry.CompressedSize > MaxCompressionRatio)
                throw UnsafeArchive("Archive có tỷ lệ nén bất thường và bị từ chối.");
            var nested = ArchiveExtensions.Contains(Path.GetExtension(key));
            entries.Add(new(
                key,
                Path.GetFileName(key),
                entry.Size,
                entry.CompressedSize,
                !nested && IsPreviewSupported(key),
                nested ? "Không preview archive lồng nhau." : UnsupportedReason(key)));
        }
        return new(submissionId, fileId, file.OriginalName, true, entries);
    }

    public async Task<SubmissionPreviewDto> GetPreviewAsync(
        Guid submissionId,
        Guid fileId,
        string? entryKey,
        string? organizationId,
        CancellationToken cancellationToken)
    {
        var file = await AuthorizedFileAsync(submissionId, fileId, organizationId, cancellationToken);
        var path = SafePhysicalPath(file.RelativePath);
        var extension = Path.GetExtension(file.OriginalName);
        byte[] bytes;
        string name;
        if (ArchiveExtensions.Contains(extension))
        {
            if (string.IsNullOrWhiteSpace(entryKey))
                throw new ApiException(ErrorCodes.ValidationFailed, "Phải chọn entry cần preview.");
            var manifest = await GetManifestAsync(submissionId, fileId, organizationId, cancellationToken);
            var selected = manifest.Entries.SingleOrDefault(x => string.Equals(x.Key, entryKey, StringComparison.Ordinal))
                ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy entry trong archive.", 404);
            if (!selected.PreviewSupported)
                throw new ApiException(ErrorCodes.ValidationFailed, selected.UnsupportedReason ?? "Định dạng không hỗ trợ preview.");
            using var archive = ArchiveFactory.OpenArchive(path);
            var archiveEntry = archive.Entries.Single(x => !x.IsDirectory && string.Equals(x.Key, entryKey, StringComparison.Ordinal));
            await using var stream = archiveEntry.OpenEntryStream();
            bytes = await ReadBoundedAsync(stream, MaxSourceBytes, cancellationToken);
            name = selected.Name;
        }
        else
        {
            if (!IsPreviewSupported(file.OriginalName))
                throw new ApiException(ErrorCodes.ValidationFailed, UnsupportedReason(file.OriginalName)!);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            bytes = await ReadBoundedAsync(stream, MaxSourceBytes, cancellationToken);
            name = file.OriginalName;
        }
        var (contentType, content, isCode) = Render(name, bytes);
        var truncated = bytes.LongLength >= MaxSourceBytes || content.Length > MaxReturnedCharacters;
        if (content.Length > MaxReturnedCharacters)
            content = content[..MaxReturnedCharacters];
        return new(submissionId, fileId, name, contentType, content, truncated, isCode, entryKey);
    }

    private async Task<SubmissionFile> AuthorizedFileAsync(
        Guid submissionId,
        Guid fileId,
        string? organizationId,
        CancellationToken cancellationToken)
    {
        var file = await db.SubmissionFilesSet.AsNoTracking()
            .Include(x => x.Submission).ThenInclude(x => x.Session).ThenInclude(x => x.Exam)
            .FirstOrDefaultAsync(
                x => x.Id == fileId && x.SubmissionId == submissionId && x.Submission.IsOfficial,
                cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy file bài nộp chính thức.", 404);
        var creator = file.Submission.Session.Exam.CreatedBy;
        if (!string.IsNullOrWhiteSpace(organizationId) && creator.HasValue)
        {
            var ownerOrganization = await db.UsersSet.AsNoTracking()
                .Where(x => x.Id == creator.Value)
                .Select(x => x.OrganizationId)
                .SingleOrDefaultAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(ownerOrganization)
                && !string.Equals(ownerOrganization, organizationId, StringComparison.Ordinal))
                throw new ApiException(ErrorCodes.Forbidden, "Không được đọc bài nộp thuộc tổ chức khác.", 403);
        }
        return file;
    }

    private string SafePhysicalPath(string relativePath)
    {
        var root = Path.GetFullPath(paths.RootPath);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new ApiException(ErrorCodes.Forbidden, "Đường dẫn file vượt khỏi storage root.", 403);
        if (!File.Exists(full))
            throw new ApiException(ErrorCodes.NotFound, "File vật lý không tồn tại.", 404);
        return full;
    }

    private static void ValidateEntryPath(string key)
    {
        var normalized = key.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.StartsWith('/')
            || Path.IsPathRooted(key)
            || normalized.Split('/').Any(x => x == ".."))
            throw UnsafeArchive("Archive chứa đường dẫn traversal không an toàn.");
    }

    private static void ValidateArchiveSignature(string path, string extension)
    {
        Span<byte> header = stackalloc byte[8];
        using var stream = File.OpenRead(path);
        var count = stream.Read(header);
        var valid = extension.ToLowerInvariant() switch
        {
            ".zip" => count >= 4 && header[0] == 0x50 && header[1] == 0x4B,
            ".rar" => count >= 7 && header[..7].SequenceEqual(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 })
                || count >= 8 && header.SequenceEqual(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 }),
            ".7z" => count >= 6 && header[..6].SequenceEqual(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }),
            _ => false
        };
        if (!valid)
            throw UnsafeArchive("Chữ ký file không khớp định dạng archive.");
    }

    private static bool IsPreviewSupported(string name)
    {
        var extension = Path.GetExtension(name);
        return TextExtensions.Contains(extension)
            || extension.Equals(".docx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    private static string? UnsupportedReason(string name) =>
        IsPreviewSupported(name) ? null : "Định dạng này không hỗ trợ preview an toàn.";

    private static (string ContentType, string Content, bool IsCode) Render(string name, byte[] bytes)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        return extension switch
        {
            ".docx" => ("text/plain", ReadDocx(bytes), false),
            ".pdf" => ("text/plain", ReadPdf(bytes), false),
            ".html" or ".htm" => ("text/plain", WebUtility.HtmlEncode(ReadText(bytes)), true),
            ".py" or ".java" => ("text/plain", ReadText(bytes), true),
            _ => ("text/plain", ReadText(bytes), false)
        };
    }

    private static string ReadDocx(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = archive.GetEntry("word/document.xml")
            ?? throw new ApiException(ErrorCodes.ValidationFailed, "DOCX không có word/document.xml.");
        using var content = entry.Open();
        var document = XDocument.Load(content);
        return string.Join(
            Environment.NewLine,
            document.Descendants()
                .Where(x => x.Name.LocalName == "p")
                .Select(x => string.Concat(x.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value)))
                .Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static string ReadPdf(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var document = PdfDocument.Open(stream);
        var text = string.Join(
            Environment.NewLine,
            document.GetPages().Select(page => ContentOrderTextExtractor.GetText(page)));
        if (string.IsNullOrWhiteSpace(text))
            throw new ApiException(ErrorCodes.ValidationFailed, "PDF không có lớp văn bản để preview.");
        return text;
    }

    private static string ReadText(byte[] bytes)
    {
        if (bytes.Any(x => x == 0))
            throw new ApiException(ErrorCodes.ValidationFailed, "File có dữ liệu nhị phân nên không thể preview dạng text.");
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream source,
        long limit,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (output.Length < limit)
        {
            var wanted = (int)Math.Min(buffer.Length, limit - output.Length);
            var read = await source.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken);
            if (read == 0)
                break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return output.ToArray();
    }

    private static ApiException UnsafeArchive(string message) =>
        new(ErrorCodes.ValidationFailed, message);
}
