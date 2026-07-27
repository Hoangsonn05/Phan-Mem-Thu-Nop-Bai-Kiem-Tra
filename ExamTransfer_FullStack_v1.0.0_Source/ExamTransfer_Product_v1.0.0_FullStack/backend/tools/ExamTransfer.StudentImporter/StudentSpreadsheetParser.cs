using System.Globalization;
using ExamTransfer.Infrastructure.Importing;

namespace ExamTransfer.StudentImporter;

internal static class StudentSpreadsheetParser
{
    public static IReadOnlyList<StudentImportRow> Read(
        string filePath,
        string emailDomain)
    {
        var bytes = File.ReadAllBytes(filePath);
        var worksheets = SpreadsheetImportReader.ReadWorksheets(
            Path.GetFileName(filePath),
            bytes);
        if (worksheets.Count == 0)
            throw new InvalidDataException("File không có dữ liệu.");

        List<List<string>>? rows = null;
        StudentImportHeaderMap? header = null;
        foreach (var worksheet in worksheets)
        {
            var candidate = StudentImportHeaderMapper.TryFindHeader(
                worksheet,
                requireDateOfBirth: true,
                out _);
            if (candidate is null)
                continue;

            rows = worksheet;
            header = candidate;
            break;
        }

        if (rows is null || header is null)
        {
            throw new InvalidDataException(
                "Không tìm thấy hàng tiêu đề. File cần có Mã sinh viên, Ngày sinh và " +
                "Họ đệm + Tên hoặc Họ và tên.");
        }

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

                var displayName = header.ReadDisplayName(row);

                if (string.IsNullOrWhiteSpace(displayName))
                    throw new InvalidDataException("Họ và tên bị trống.");

                var dateText = ReadCell(row, header.DateOfBirthColumn!.Value).Trim();
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

}
