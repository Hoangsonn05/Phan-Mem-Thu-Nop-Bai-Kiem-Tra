using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ExamTransfer.Application;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Infrastructure.Importing;

/// <summary>
/// Reads CSV and XLSX rows without requiring Microsoft Office or a heavyweight spreadsheet package.
/// The reader intentionally supports the first worksheet because the frontend import workflow maps
/// columns after preview and does not need workbook editing features.
/// </summary>
public static class SpreadsheetImportReader
{
    private const int MaxInputBytes = 20 * 1024 * 1024;
    private const int MaxRows = 50_000;
    private const int MaxColumns = 256;

    public static List<List<string>> ReadRows(string fileName, byte[] bytes)
    {
        if (bytes.Length == 0)
            throw new ApiException(ErrorCodes.ValidationFailed, "File import rỗng.");

        if (bytes.Length > MaxInputBytes)
            throw new ApiException(ErrorCodes.FileTooLarge, "File import vượt quá 20 MiB.");

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".csv" => ParseCsv(DecodeText(bytes)),
            ".xlsx" => ParseXlsx(bytes),
            _ => throw new ApiException(
                ErrorCodes.InvalidFileType,
                "Chỉ hỗ trợ file CSV hoặc XLSX.")
        };
    }

    public static string EscapeCsv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    private static string DecodeText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    AddRow(rows, row);
                    row = [];
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            AddRow(rows, row);
        }

        return rows;
    }

    private static List<List<string>> ParseXlsx(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

            var sharedStrings = ReadSharedStrings(archive);
            var worksheetPath = ResolveFirstWorksheetPath(archive);
            var worksheet = archive.GetEntry(worksheetPath)
                ?? throw new ApiException(
                    ErrorCodes.ValidationFailed,
                    "Không tìm thấy worksheet đầu tiên trong file XLSX.");

            using var worksheetStream = worksheet.Open();
            var document = XDocument.Load(worksheetStream, LoadOptions.None);
            var rows = new List<List<string>>();

            foreach (var rowElement in document
                         .Descendants()
                         .Where(x => x.Name.LocalName == "row"))
            {
                if (rows.Count >= MaxRows)
                    throw new ApiException(
                        ErrorCodes.ValidationFailed,
                        $"File XLSX vượt quá giới hạn {MaxRows:N0} dòng.");

                var values = new SortedDictionary<int, string>();
                var nextColumn = 0;

                foreach (var cell in rowElement.Elements()
                             .Where(x => x.Name.LocalName == "c"))
                {
                    var reference = cell.Attribute("r")?.Value;
                    var column = string.IsNullOrWhiteSpace(reference)
                        ? nextColumn
                        : GetColumnIndex(reference);

                    if (column < 0 || column >= MaxColumns)
                        continue;

                    values[column] = ReadCellValue(cell, sharedStrings);
                    nextColumn = column + 1;
                }

                if (values.Count == 0)
                    continue;

                var lastColumn = values.Keys.Max();
                var row = Enumerable.Repeat(string.Empty, lastColumn + 1).ToList();
                foreach (var pair in values)
                    row[pair.Key] = pair.Value;

                AddRow(rows, row);
            }

            return rows;
        }
        catch (ApiException)
        {
            throw;
        }
        catch (InvalidDataException ex)
        {
            throw new ApiException(
                ErrorCodes.ValidationFailed,
                "File XLSX bị hỏng hoặc không đúng định dạng.",
                details: ex.Message);
        }
        catch (Exception ex)
        {
            throw new ApiException(
                ErrorCodes.ValidationFailed,
                "Không thể đọc file XLSX.",
                details: ex.Message);
        }
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
            return Array.Empty<string>();

        using var stream = entry.Open();
        var document = XDocument.Load(stream, LoadOptions.None);

        return document.Descendants()
            .Where(x => x.Name.LocalName == "si")
            .Select(si => string.Concat(
                si.Descendants()
                    .Where(x => x.Name.LocalName == "t")
                    .Select(x => x.Value)))
            .ToList();
    }

    private static string ResolveFirstWorksheetPath(ZipArchive archive)
    {
        const string defaultPath = "xl/worksheets/sheet1.xml";
        var workbook = archive.GetEntry("xl/workbook.xml");
        var relationships = archive.GetEntry("xl/_rels/workbook.xml.rels");

        if (workbook is null || relationships is null)
            return defaultPath;

        using var workbookStream = workbook.Open();
        var workbookDocument = XDocument.Load(workbookStream, LoadOptions.None);
        var firstSheet = workbookDocument.Descendants()
            .FirstOrDefault(x => x.Name.LocalName == "sheet");

        var relationshipId = firstSheet?.Attributes()
            .FirstOrDefault(x => x.Name.LocalName == "id")
            ?.Value;

        if (string.IsNullOrWhiteSpace(relationshipId))
            return defaultPath;

        using var relationshipStream = relationships.Open();
        var relationshipDocument = XDocument.Load(
            relationshipStream,
            LoadOptions.None);

        var relationship = relationshipDocument.Descendants()
            .FirstOrDefault(x =>
                x.Name.LocalName == "Relationship"
                && string.Equals(
                    x.Attribute("Id")?.Value,
                    relationshipId,
                    StringComparison.Ordinal));

        var target = relationship?.Attribute("Target")?.Value;
        if (string.IsNullOrWhiteSpace(target))
            return defaultPath;

        target = target.Replace('\\', '/').TrimStart('/');
        if (target.StartsWith("../", StringComparison.Ordinal))
            target = target[3..];

        return target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)
            ? target
            : "xl/" + target;
    }

    private static string ReadCellValue(
        XElement cell,
        IReadOnlyList<string> sharedStrings)
    {
        var type = cell.Attribute("t")?.Value;
        var raw = cell.Elements()
            .FirstOrDefault(x => x.Name.LocalName == "v")
            ?.Value;

        if (type == "inlineStr")
        {
            return string.Concat(cell.Descendants()
                .Where(x => x.Name.LocalName == "t")
                .Select(x => x.Value));
        }

        if (type == "s"
            && int.TryParse(
                raw,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var sharedIndex)
            && sharedIndex >= 0
            && sharedIndex < sharedStrings.Count)
        {
            return sharedStrings[sharedIndex];
        }

        if (type == "b")
            return raw == "1" ? "TRUE" : "FALSE";

        return raw ?? string.Empty;
    }

    private static int GetColumnIndex(string cellReference)
    {
        var index = 0;
        var hasLetter = false;

        foreach (var c in cellReference)
        {
            if (!char.IsLetter(c))
                break;

            hasLetter = true;
            index = checked(index * 26 + (char.ToUpperInvariant(c) - 'A' + 1));
        }

        return hasLetter ? index - 1 : -1;
    }

    private static void AddRow(
        ICollection<List<string>> rows,
        List<string> row)
    {
        if (row.Any(value => !string.IsNullOrWhiteSpace(value)))
            rows.Add(row);
    }
}
