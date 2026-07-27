using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ExamTransfer.Application;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Infrastructure.Importing;

/// <summary>
/// Reads CSV and XLSX rows without requiring Microsoft Office or a heavyweight spreadsheet package.
/// XLSX callers can inspect worksheets in workbook order and select the first sheet whose
/// headers match their import contract.
/// </summary>
public static class SpreadsheetImportReader
{
    private const int MaxInputBytes = 20 * 1024 * 1024;
    private const int MaxRows = 50_000;
    private const int MaxColumns = 256;

    public static List<List<string>> ReadRows(string fileName, byte[] bytes)
    {
        var worksheets = ReadWorksheets(fileName, bytes);
        return worksheets.FirstOrDefault() ?? [];
    }

    public static IReadOnlyList<List<List<string>>> ReadWorksheets(
        string fileName,
        byte[] bytes)
    {
        if (bytes.Length == 0)
            throw new ApiException(ErrorCodes.ValidationFailed, "File import rỗng.");

        if (bytes.Length > MaxInputBytes)
            throw new ApiException(ErrorCodes.FileTooLarge, "File import vượt quá 20 MiB.");

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".csv" => [ParseCsv(DecodeText(bytes))],
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
        var delimiter = DetectDelimiter(text);
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
                case var value when value == delimiter:
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

    private static char DetectDelimiter(string text)
    {
        var candidates = new[] { ',', ';', '\t' };
        var scores = candidates.ToDictionary(candidate => candidate, _ => 0);
        var rowScores = candidates.ToDictionary(candidate => candidate, _ => 0);
        var quoted = false;
        var rows = 0;

        for (var index = 0; index < text.Length && rows < 30; index++)
        {
            var current = text[index];
            if (current == '"')
            {
                if (quoted && index + 1 < text.Length && text[index + 1] == '"')
                {
                    index++;
                    continue;
                }
                quoted = !quoted;
                continue;
            }
            if (quoted)
                continue;
            if (rowScores.ContainsKey(current))
                rowScores[current]++;
            if (current != '\n')
                continue;

            foreach (var candidate in candidates)
                scores[candidate] = Math.Max(scores[candidate], rowScores[candidate]);
            foreach (var candidate in candidates)
                rowScores[candidate] = 0;
            rows++;
        }

        foreach (var candidate in candidates)
            scores[candidate] = Math.Max(scores[candidate], rowScores[candidate]);
        return candidates
            .OrderByDescending(candidate => scores[candidate])
            .ThenBy(candidate => Array.IndexOf(candidates, candidate))
            .First();
    }

    private static IReadOnlyList<List<List<string>>> ParseXlsx(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

            var sharedStrings = ReadSharedStrings(archive);
            var worksheets = new List<List<List<string>>>();
            foreach (var worksheetPath in ResolveWorksheetPaths(archive))
            {
                var worksheet = archive.GetEntry(worksheetPath);
                if (worksheet is null)
                    continue;
                var rows = ReadWorksheet(worksheet, sharedStrings);
                if (rows.Count > 0)
                    worksheets.Add(rows);
            }

            if (worksheets.Count == 0)
                throw new ApiException(
                    ErrorCodes.ValidationFailed,
                    "Không tìm thấy worksheet có dữ liệu trong file XLSX.");
            return worksheets;
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

    private static List<List<string>> ReadWorksheet(
        ZipArchiveEntry worksheet,
        IReadOnlyList<string> sharedStrings)
    {
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

    private static IReadOnlyList<string> ResolveWorksheetPaths(ZipArchive archive)
    {
        const string defaultPath = "xl/worksheets/sheet1.xml";
        var workbook = archive.GetEntry("xl/workbook.xml");
        var relationships = archive.GetEntry("xl/_rels/workbook.xml.rels");

        if (workbook is null || relationships is null)
            return [defaultPath];

        using var workbookStream = workbook.Open();
        var workbookDocument = XDocument.Load(workbookStream, LoadOptions.None);
        var relationshipIds = workbookDocument.Descendants()
            .Where(x => x.Name.LocalName == "sheet")
            .Select(sheet => sheet.Attributes()
                .FirstOrDefault(attribute => attribute.Name.LocalName == "id")
                ?.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToList();
        if (relationshipIds.Count == 0)
            return [defaultPath];

        using var relationshipStream = relationships.Open();
        var relationshipDocument = XDocument.Load(
            relationshipStream,
            LoadOptions.None);

        var targets = relationshipIds
            .Select(id => relationshipDocument.Descendants()
                .FirstOrDefault(x =>
                    x.Name.LocalName == "Relationship"
                    && string.Equals(
                        x.Attribute("Id")?.Value,
                        id,
                        StringComparison.Ordinal))
                ?.Attribute("Target")?.Value)
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Select(target =>
            {
                var normalized = target!.Replace('\\', '/').TrimStart('/');
                if (normalized.StartsWith("../", StringComparison.Ordinal))
                    normalized = normalized[3..];
                return normalized.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)
                    ? normalized
                    : "xl/" + normalized;
            })
            .ToList();
        return targets.Count > 0 ? targets : [defaultPath];
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
