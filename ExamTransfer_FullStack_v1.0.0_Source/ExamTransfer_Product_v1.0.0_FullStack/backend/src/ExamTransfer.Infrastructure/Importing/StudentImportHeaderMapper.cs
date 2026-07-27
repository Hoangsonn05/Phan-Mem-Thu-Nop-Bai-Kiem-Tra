using System.Globalization;
using System.Text;

namespace ExamTransfer.Infrastructure.Importing;

public sealed record StudentImportHeaderMap(
    int RowIndex,
    int StudentCodeColumn,
    int? FullNameColumn,
    IReadOnlyList<int> FamilyAndMiddleNameColumns,
    int? GivenNameColumn,
    int? EmailColumn,
    int? DateOfBirthColumn)
{
    public string ReadDisplayName(IReadOnlyList<string> row)
    {
        if (FullNameColumn is { } fullNameColumn)
            return StudentImportHeaderMapper.NormalizeSpaces(ReadCell(row, fullNameColumn));

        var parts = FamilyAndMiddleNameColumns
            .Select(column => ReadCell(row, column))
            .Append(GivenNameColumn is { } givenNameColumn
                ? ReadCell(row, givenNameColumn)
                : string.Empty);
        return StudentImportHeaderMapper.NormalizeSpaces(string.Join(' ', parts));
    }

    public static string ReadCell(IReadOnlyList<string> row, int column) =>
        column >= 0 && column < row.Count ? row[column] ?? string.Empty : string.Empty;
}

public static class StudentImportHeaderMapper
{
    private static readonly HashSet<string> StudentCodeAliases = CanonicalSet(
        "Mã sinh viên", "Ma sinh vien", "Mã SV", "Ma SV", "MSSV",
        "Student Code", "StudentCode", "student_code", "Mã học sinh");
    private static readonly HashSet<string> FullNameAliases = CanonicalSet(
        "Họ và tên", "Ho va ten", "Họ tên", "Ho ten",
        "Display Name", "DisplayName", "Name");
    private static readonly HashSet<string> FamilyAndMiddleNameAliases = CanonicalSet(
        "Họ", "Họ đệm", "Họ và tên đệm", "Tên đệm", "Last Name");
    private static readonly HashSet<string> GivenNameAliases = CanonicalSet(
        "Tên", "First Name");
    private static readonly HashSet<string> EmailAliases = CanonicalSet(
        "Email", "E-mail", "Địa chỉ email");
    private static readonly HashSet<string> DateOfBirthAliases = CanonicalSet(
        "Ngày sinh", "Ngay sinh", "Date of birth", "DOB");

    public static StudentImportHeaderMap? TryFindHeader(
        IReadOnlyList<List<string>> rows,
        bool requireDateOfBirth,
        out IReadOnlyList<string> observedHeaders,
        IReadOnlyDictionary<string, string>? columnMapping = null)
    {
        var observed = new List<string>();
        for (var rowIndex = 0; rowIndex < Math.Min(rows.Count, 30); rowIndex++)
        {
            var normalized = rows[rowIndex].Select(NormalizeHeader).ToList();
            if (normalized.Any(value => value.Length > 0))
                observed.Add(string.Join(" | ", rows[rowIndex].Select(value => value.Trim())));

            var studentCode = FindMappedOrAliasColumn(
                normalized,
                columnMapping,
                "studentCode",
                StudentCodeAliases);
            var fullName = FindMappedOrAliasColumn(
                normalized,
                columnMapping,
                "displayName",
                FullNameAliases);
            var familyAndMiddle = FindColumns(normalized, FamilyAndMiddleNameAliases);
            var givenName = FindColumn(normalized, GivenNameAliases);
            var email = FindMappedOrAliasColumn(
                normalized,
                columnMapping,
                "email",
                EmailAliases);
            var dateOfBirth = FindMappedOrAliasColumn(
                normalized,
                columnMapping,
                "dateOfBirth",
                DateOfBirthAliases);
            var hasName = fullName is not null
                || (familyAndMiddle.Count > 0 && givenName is not null);

            if (studentCode is not null
                && hasName
                && (!requireDateOfBirth || dateOfBirth is not null))
            {
                observedHeaders = observed;
                return new(
                    rowIndex,
                    studentCode.Value,
                    fullName,
                    familyAndMiddle,
                    givenName,
                    email,
                    dateOfBirth);
            }
        }

        observedHeaders = observed;
        return null;
    }

    public static string NormalizeHeader(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        value = value
            .TrimStart('\uFEFF')
            .Replace('\u00A0', ' ')
            .Replace('Đ', 'D')
            .Replace('đ', 'd');
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var previousWasSpace = true;

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        return NormalizeSpaces(builder.ToString().Normalize(NormalizationForm.FormC));
    }

    public static string NormalizeSpaces(string value) =>
        string.Join(' ', value
            .Replace('\u00A0', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static HashSet<string> CanonicalSet(params string[] aliases) =>
        aliases.Select(NormalizeHeader).ToHashSet(StringComparer.Ordinal);

    private static int? FindColumn(
        IReadOnlyList<string> headers,
        IReadOnlySet<string> aliases)
    {
        for (var index = 0; index < headers.Count; index++)
        {
            if (aliases.Contains(headers[index]))
                return index;
        }
        return null;
    }

    private static int? FindMappedOrAliasColumn(
        IReadOnlyList<string> headers,
        IReadOnlyDictionary<string, string>? mapping,
        string key,
        IReadOnlySet<string> aliases)
    {
        if (mapping is null || !mapping.TryGetValue(key, out var mapped))
            return FindColumn(headers, aliases);

        var normalizedTarget = NormalizeHeader(mapped);
        for (var index = 0; index < headers.Count; index++)
        {
            if (headers[index] == normalizedTarget)
                return index;
        }
        return null;
    }

    private static IReadOnlyList<int> FindColumns(
        IReadOnlyList<string> headers,
        IReadOnlySet<string> aliases) =>
        headers
            .Select((header, index) => new { header, index })
            .Where(item => aliases.Contains(item.header))
            .Select(item => item.index)
            .ToList();
}
