using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ExamTransfer.Shared.Contracts;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace ExamTransfer.Infrastructure.Services;

internal sealed record QuizDocumentParseResult(
    QuizImportDocument Document,
    IReadOnlyList<QuizImportIssueDto> Warnings,
    IReadOnlyList<QuizImportIssueDto> Errors);

internal static partial class QuizDocumentParser
{
    private const int MaxExpandedDocumentBytes = 32 * 1024 * 1024;

    public static QuizDocumentParseResult Parse(string fileName, byte[] bytes)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        try
        {
            var lines = extension switch
            {
                ".docx" => ExtractDocxLines(bytes),
                ".pdf" => ExtractPdfLines(bytes),
                _ => throw new InvalidDataException("Chỉ chấp nhận nguồn trắc nghiệm DOCX hoặc PDF.")
            };
            return ParseLines(lines);
        }
        catch (InvalidDataException ex)
        {
            return new(new([]), [], [new(null, null, "DOCUMENT_INVALID", ex.Message)]);
        }
        catch (Exception ex)
        {
            return new(new([]), [], [new(null, null, "DOCUMENT_READ_FAILED", $"Không đọc được tài liệu: {ex.Message}")]);
        }
    }

    private static IReadOnlyList<(int Line, string Text)> ExtractDocxLines(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Sum(x => x.Length) > MaxExpandedDocumentBytes
            || archive.Entries.Any(x => x.Length > MaxExpandedDocumentBytes))
            throw new InvalidDataException("DOCX giải nén vượt giới hạn an toàn 32 MB.");
        var entry = archive.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("DOCX không có word/document.xml.");
        XDocument document;
        using (var content = entry.Open())
            document = XDocument.Load(content, LoadOptions.PreserveWhitespace);
        var body = document.Descendants().FirstOrDefault(x => x.Name.LocalName == "body")
            ?? throw new InvalidDataException("DOCX không có phần nội dung.");
        var result = new List<(int Line, string Text)>();
        var line = 0;
        foreach (var block in body.Elements())
        {
            if (block.Name.LocalName == "p")
            {
                AddParagraph(block, result, ref line);
                continue;
            }
            if (block.Name.LocalName != "tbl")
                continue;
            foreach (var row in block.Elements().Where(x => x.Name.LocalName == "tr"))
            foreach (var cell in row.Elements().Where(x => x.Name.LocalName == "tc"))
            foreach (var paragraph in cell.Elements().Where(x => x.Name.LocalName == "p"))
                AddParagraph(paragraph, result, ref line);
        }
        if (result.Count == 0)
            throw new InvalidDataException("DOCX không có văn bản có thể đọc.");
        return result;
    }

    private static void AddParagraph(XElement paragraph, List<(int Line, string Text)> result, ref int line)
    {
        var text = string.Concat(paragraph.Descendants()
            .Where(x => x.Name.LocalName is "t" or "tab" or "br")
            .Select(x => x.Name.LocalName switch
            {
                "tab" => "\t",
                "br" => "\n",
                _ => x.Value
            }));
        foreach (var part in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
            result.Add((++line, part));
    }

    private static IReadOnlyList<(int Line, string Text)> ExtractPdfLines(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var document = PdfDocument.Open(stream);
        var result = new List<(int Line, string Text)>();
        var line = 0;
        foreach (var page in document.GetPages())
        {
            var text = ContentOrderTextExtractor.GetText(page);
            foreach (var part in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
                result.Add((++line, part));
        }
        if (result.All(x => string.IsNullOrWhiteSpace(x.Text)))
            throw new InvalidDataException("PDF không có lớp văn bản; hãy dùng PDF có thể chọn chữ hoặc DOCX.");
        return result;
    }

    private static QuizDocumentParseResult ParseLines(IReadOnlyList<(int Line, string Text)> source)
    {
        var warnings = new List<QuizImportIssueDto>();
        var errors = new List<QuizImportIssueDto>();
        var questions = new List<QuizImportQuestion>();
        DraftQuestion? current = null;
        string? currentChoiceLabel = null;

        void CloseCurrent()
        {
            if (current is null)
                return;
            ValidateAndAppend(current, questions, errors);
            current = null;
            currentChoiceLabel = null;
        }

        foreach (var (lineNumber, raw) in source)
        {
            var line = NormalizeWhitespace(raw);
            if (line.Length == 0)
                continue;

            var answerMatch = AnswerLineRegex().Match(line);
            if (answerMatch.Success)
            {
                if (current is null)
                {
                    errors.Add(new(null, lineNumber, "ANSWER_WITHOUT_QUESTION", "Dòng đáp án không gắn với câu hỏi nào."));
                    continue;
                }
                if (current.AnswerLine.HasValue)
                {
                    errors.Add(new(current.Number, lineNumber, "DUPLICATE_ANSWER", "Câu hỏi có nhiều hơn một dòng đáp án."));
                    continue;
                }
                current.AnswerLine = lineNumber;
                ParseAnswer(current, answerMatch.Groups["answer"].Value, errors);
                currentChoiceLabel = null;
                continue;
            }

            var choiceMatch = ChoiceLineRegex().Match(line);
            if (choiceMatch.Success)
            {
                if (current is null)
                {
                    errors.Add(new(null, lineNumber, "CHOICE_WITHOUT_QUESTION", "Lựa chọn không gắn với câu hỏi nào."));
                    continue;
                }
                var label = choiceMatch.Groups["label"].Value.ToUpperInvariant();
                if (current.Choices.ContainsKey(label))
                {
                    errors.Add(new(current.Number, lineNumber, "DUPLICATE_CHOICE", $"Lựa chọn {label} bị lặp."));
                    continue;
                }
                current.Choices[label] = new(lineNumber, choiceMatch.Groups["text"].Value.Trim());
                currentChoiceLabel = label;
                continue;
            }

            if (current is null || current.AnswerLine.HasValue)
            {
                CloseCurrent();
                current = new DraftQuestion(questions.Count + 1, lineNumber, StripQuestionPrefix(line));
                continue;
            }

            if (current.Choices.Count == 0)
            {
                current.Text = $"{current.Text} {line}".Trim();
            }
            else if (currentChoiceLabel is not null)
            {
                var existing = current.Choices[currentChoiceLabel];
                current.Choices[currentChoiceLabel] = existing with { Text = $"{existing.Text} {line}".Trim() };
            }
            else
            {
                errors.Add(new(current.Number, lineNumber, "QUESTION_NOT_CLOSED", "Câu hỏi mới xuất hiện trước khi câu hiện tại có dòng đáp án."));
            }
        }
        CloseCurrent();

        if (questions.Count is < 1 or > 500)
            errors.Add(new(null, null, "QUESTION_COUNT", "Đề phải có từ 1 đến 500 câu hỏi hợp lệ."));
        return new(new(questions), warnings, errors);
    }

    private static void ParseAnswer(
        DraftQuestion question,
        string answerText,
        List<QuizImportIssueDto> errors)
    {
        var parts = answerText.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var match = AnswerPartRegex().Match(part);
            if (!match.Success)
            {
                errors.Add(new(question.Number, question.AnswerLine, "ANSWER_FORMAT", $"Không đọc được đáp án '{part}'. Dùng A, A;C hoặc A. Nội dung."));
                continue;
            }
            var label = match.Groups["label"].Value.ToUpperInvariant();
            question.CorrectLabels.Add(label);
            var expectedText = match.Groups["text"].Value.Trim();
            if (expectedText.Length == 0)
                continue;
            if (!question.Choices.TryGetValue(label, out var choice)
                || !NormalizeForComparison(choice.Text).Equals(NormalizeForComparison(expectedText), StringComparison.Ordinal))
                errors.Add(new(question.Number, question.AnswerLine, "ANSWER_TEXT_MISMATCH", $"Nội dung đáp án {label} không khớp nội dung lựa chọn."));
        }
    }

    private static void ValidateAndAppend(
        DraftQuestion question,
        List<QuizImportQuestion> output,
        List<QuizImportIssueDto> errors)
    {
        var before = errors.Count;
        if (string.IsNullOrWhiteSpace(question.Text) || question.Text.Length > 5000)
            errors.Add(new(question.Number, question.StartLine, "QUESTION_TEXT", "Nội dung câu hỏi phải có từ 1 đến 5000 ký tự."));
        if (question.Choices.Count is < 2 or > 10)
            errors.Add(new(question.Number, question.StartLine, "CHOICE_COUNT", "Mỗi câu phải có từ 2 đến 10 lựa chọn."));
        foreach (var choice in question.Choices)
            if (string.IsNullOrWhiteSpace(choice.Value.Text) || choice.Value.Text.Length > 5000)
                errors.Add(new(question.Number, choice.Value.Line, "CHOICE_TEXT", $"Lựa chọn {choice.Key} không được trống và tối đa 5000 ký tự."));
        if (!question.AnswerLine.HasValue)
            errors.Add(new(question.Number, question.StartLine, "ANSWER_MISSING", "Thiếu dòng 'Đáp án đúng:'."));
        if (question.CorrectLabels.Count == 0)
            errors.Add(new(question.Number, question.AnswerLine, "ANSWER_MISSING", "Câu hỏi phải có ít nhất một đáp án đúng."));
        foreach (var label in question.CorrectLabels)
            if (!question.Choices.ContainsKey(label))
                errors.Add(new(question.Number, question.AnswerLine, "ANSWER_UNKNOWN_CHOICE", $"Đáp án {label} không tồn tại trong các lựa chọn."));
        if (errors.Count != before)
            return;
        var ordered = question.Choices.OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
        var correctIndexes = ordered.Select((choice, index) => (choice.Key, index))
            .Where(x => question.CorrectLabels.Contains(x.Key))
            .Select(x => x.index)
            .ToList();
        output.Add(new(question.Text, 1m, correctIndexes.Count > 1, ordered.Select(x => x.Value.Text).ToList(), correctIndexes));
    }

    private static string StripQuestionPrefix(string value)
    {
        var match = QuestionPrefixRegex().Match(value);
        return match.Success ? match.Groups["text"].Value.Trim() : value.Trim();
    }

    private static string NormalizeWhitespace(string value) =>
        WhitespaceRegex().Replace(value.Normalize(), " ").Trim();

    private static string NormalizeForComparison(string value) =>
        NormalizeWhitespace(value).ToUpperInvariant();

    [GeneratedRegex(@"^\s*(?:Đáp\s*án\s*đúng|Dap\s*an\s*dung|Đáp\s*án|Answer)\s*:\s*(?<answer>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnswerLineRegex();

    [GeneratedRegex(@"^\s*(?<label>[A-J])[\.\)]\s*(?<text>.*?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChoiceLineRegex();

    [GeneratedRegex(@"^\s*(?<label>[A-J])(?:[\.\)]\s*(?<text>.*))?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnswerPartRegex();

    [GeneratedRegex(@"^\s*(?:\d+(?:[_\.]\d+)*|Câu\s+\d+)\s*[:\.\)]\s*(?<text>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuestionPrefixRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private sealed class DraftQuestion(int number, int startLine, string text)
    {
        public int Number { get; } = number;
        public int StartLine { get; } = startLine;
        public string Text { get; set; } = text;
        public SortedDictionary<string, DraftChoice> Choices { get; } = new(StringComparer.Ordinal);
        public HashSet<string> CorrectLabels { get; } = new(StringComparer.Ordinal);
        public int? AnswerLine { get; set; }
    }

    private sealed record DraftChoice(int Line, string Text);
}
