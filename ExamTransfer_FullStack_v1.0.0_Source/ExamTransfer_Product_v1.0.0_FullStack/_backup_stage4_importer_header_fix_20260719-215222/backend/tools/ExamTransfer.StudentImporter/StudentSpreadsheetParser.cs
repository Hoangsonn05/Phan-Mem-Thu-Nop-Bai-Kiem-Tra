using System.Globalization;
using System.Text;
using ExamTransfer.Infrastructure.Importing;

namespace ExamTransfer.StudentImporter;

internal static class StudentSpreadsheetParser
{
    private static readonly string[] StudentCodeAliases =
        ["ma sinh vien", "masv", "student code", "studentcode"];
    private static readonly string[] MiddleNameAliases =
        ["ho dem", "ho va dem", "ho lot", "surname middle name"];
    private static readonly string[] FirstNameAliases =
        ["ten", "first name", "firstname"];
    private static readonly string[] FullNameAliases =
        ["ho va ten", "ho ten", "fullname", "full name"];
    private static readonly string[] DateOfBirthAliases =
        ["ngay sinh", "date of birth", "dateofbirth", "dob"];

    public static IReadOnlyList<StudentImportRow> Read(
        string filePath,
        string emailDomain)
    {
        var bytes = File.ReadAllBytes(filePath);
        var rows = SpreadsheetImportReader.ReadRows(Path.GetFileName(filePath), bytes);
        if (rows.Count == 0)
            throw new InvalidDataException("File không có dữ liệu.");

        var header = FindHeader(rows);
        var result = new List<StudentImportRow>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        for (var rowIndex = header.RowIndex + 1; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row.All(string.IsNullOrWhiteSpace))
                continue;

            var sourceRow = rowIndex + 1;
            try
            {
                var studentCode = ReadCell(row, header.StudentCodeColumn).Trim();
                if (string.IsNullOrWhiteSpace(studentCode))
                    continue;

                if (studentCode.Length is < 5 or > 32 || studentCode.Any(ch => ch is < '0' or > '9'))
                    throw new InvalidDataException("Mã sinh viên chỉ được chứa chữ số và dài 5-32 ký tự.");

                if (!seenCodes.Add(studentCode))
                    throw new InvalidDataException("Mã sinh viên bị trùng trong file.");

                var displayName = header.FullNameColumn is not null
                    ? NormalizeSpaces(ReadCell(row, header.FullNameColumn.Value))
                    : NormalizeSpaces(
                        $"{ReadCell(row, header.MiddleNameColumn!.Value)} {ReadCell(row, header.FirstNameColumn!.Value)}");

                if (string.IsNullOrWhiteSpace(displayName))
                    throw new InvalidDataException("Họ và tên bị trống.");

                var dateText = ReadCell(row, header.DateOfBirthColumn).Trim();
                var dateOfBirth = ParseDate(dateText);
                if (dateOfBirth < new DateOnly(1900, 1, 1) || dateOfBirth > DateOnly.FromDateTime(DateTime.Today))
                    throw new InvalidDataException("Ngày sinh nằm ngoài khoảng hợp lệ.");

                result.Add(new StudentImportRow(
                    sourceRow,
                    studentCode,
                    displayName,
                    dateOfBirth,
                    $"{studentCode}@{emailDomain}".ToLowerInvariant()));
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException)
            {
                errors.Add($"Dòng Excel {sourceRow}: {ex.Message}");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                "Danh sách sinh viên chưa hợp lệ:\n- " + string.Join("\n- ", errors));
        }

        if (result.Count == 0)
            throw new InvalidDataException("Không tìm thấy sinh viên hợp lệ trong file.");

        return result;
    }

    private static HeaderMap FindHeader(IReadOnlyList<List<string>> rows)
    {
        for (var rowIndex = 0; rowIndex < Math.Min(rows.Count, 30); rowIndex++)
        {
            var normalized = rows[rowIndex]
                .Select(NormalizeHeader)
                .ToList();

            var studentCode = FindColumn(normalized, StudentCodeAliases);
            var dateOfBirth = FindColumn(normalized, DateOfBirthAliases);
            var fullName = FindColumn(normalized, FullNameAliases);
            var middleName = FindColumn(normalized, MiddleNameAliases);
            var firstName = FindColumn(normalized, FirstNameAliases);

            if (studentCode is not null
                && dateOfBirth is not null
                && (fullName is not null || (middleName is not null && firstName is not null)))
            {
                return new HeaderMap(
                    rowIndex,
                    studentCode.Value,
                    middleName,
                    firstName,
                    fullName,
                    dateOfBirth.Value);
            }
        }

        throw new InvalidDataException(
            "Không tìm thấy hàng tiêu đề. File cần có Mã sinh viên, Ngày sinh và " +
            "Họ đệm + Tên hoặc Họ và tên.");
    }

    private static int? FindColumn(IReadOnlyList<string> headers, IEnumerable<string> aliases)
    {
        var aliasSet = aliases.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Count; i++)
        {
            if (aliasSet.Contains(headers[i]))
                return i;
        }

        return null;
    }

    private static string ReadCell(IReadOnlyList<string> row, int column) =>
        column >= 0 && column < row.Count ? row[column] ?? string.Empty : string.Empty;

    private static DateOnly ParseDate(string value)
    {
        var formats = new[]
        {
            "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "yyyy-MM-dd"
        };

        if (DateOnly.TryParseExact(
                value,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return parsed;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial)
            && serial is >= 1 and <= 2_958_465)
        {
            return DateOnly.FromDateTime(DateTime.FromOADate(serial));
        }

        throw new FormatException($"Ngày sinh '{value}' không đúng định dạng dd/MM/yyyy.");
    }

    private static string NormalizeSpaces(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string NormalizeHeader(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(ch));
        }

        return NormalizeSpaces(builder.ToString().Normalize(NormalizationForm.FormC));
    }

    private sealed record HeaderMap(
        int RowIndex,
        int StudentCodeColumn,
        int? MiddleNameColumn,
        int? FirstNameColumn,
        int? FullNameColumn,
        int DateOfBirthColumn);
}
