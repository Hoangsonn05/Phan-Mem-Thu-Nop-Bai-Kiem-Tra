using System.IO.Compression;
using System.Text;
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

internal enum DocumentBlockKind
{
    DocxParagraph,
    DocxTableParagraph,
    PdfLine
}

internal sealed record StyledSpan(string Text, bool Bold, bool Highlighted, bool Shaded);

internal sealed record DocumentBlock(
    int Line,
    string Text,
    DocumentBlockKind Kind,
    IReadOnlyList<StyledSpan> Spans,
    bool IsPredominantlyBold,
    bool HasHighlight,
    bool HasShading);

internal static partial class QuizDocumentParser
{
    private const int MaxExpandedDocumentBytes = 32 * 1024 * 1024;
    private const string SyntheticNoneChoice = "Không có đáp án đúng trong các lựa chọn";

    public static QuizDocumentParseResult Parse(string fileName, byte[] bytes)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        try
        {
            var blocks = extension switch
            {
                ".docx" => ExtractDocxBlocks(bytes),
                ".pdf" => ExtractPdfBlocks(bytes),
                _ => throw new InvalidDataException("Chỉ chấp nhận nguồn trắc nghiệm DOCX hoặc PDF.")
            };
            return ParseBlocks(blocks);
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

    private static IReadOnlyList<DocumentBlock> ExtractDocxBlocks(byte[] bytes)
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
        var styles = new DocxStyleResolver(archive);
        var result = new List<DocumentBlock>();
        var line = 0;
        foreach (var block in body.Elements())
        {
            if (block.Name.LocalName == "p")
            {
                AddParagraph(block, DocumentBlockKind.DocxParagraph, styles, result, ref line);
                continue;
            }
            if (block.Name.LocalName != "tbl")
                continue;
            foreach (var paragraph in block.Descendants().Where(x => x.Name.LocalName == "p"))
                AddParagraph(paragraph, DocumentBlockKind.DocxTableParagraph, styles, result, ref line);
        }
        if (result.All(x => string.IsNullOrWhiteSpace(x.Text)))
            throw new InvalidDataException("DOCX không có văn bản có thể đọc.");
        return result;
    }

    private static void AddParagraph(
        XElement paragraph,
        DocumentBlockKind kind,
        DocxStyleResolver styles,
        List<DocumentBlock> result,
        ref int line)
    {
        var paragraphFormat = styles.ResolveParagraph(paragraph);
        var current = new List<StyledSpan>();
        var currentLine = line;

        void CloseLine()
        {
            currentLine++;
            var text = string.Concat(current.Select(x => x.Text));
            result.Add(CreateBlock(currentLine, text, kind, current));
            current = [];
        }

        foreach (var run in paragraph.Descendants().Where(x => x.Name.LocalName == "r"))
        {
            var format = styles.ResolveRun(run, paragraphFormat);
            foreach (var value in run.Descendants().Where(x => x.Name.LocalName is "t" or "tab" or "br" or "cr"))
            {
                if (value.Name.LocalName is "br" or "cr")
                {
                    CloseLine();
                    continue;
                }
                var text = value.Name.LocalName == "tab" ? "\t" : value.Value;
                if (text.Length > 0)
                    current.Add(new(text, format.Bold, format.Highlighted, format.Shaded));
            }
        }
        CloseLine();
        line = currentLine;
    }

    private static DocumentBlock CreateBlock(
        int line,
        string text,
        DocumentBlockKind kind,
        IReadOnlyList<StyledSpan> spans)
    {
        var total = spans.Sum(x => x.Text.Count(c => !char.IsWhiteSpace(c)));
        var bold = spans.Where(x => x.Bold).Sum(x => x.Text.Count(c => !char.IsWhiteSpace(c)));
        return new(
            line,
            text,
            kind,
            spans.ToArray(),
            total > 0 && bold * 2 > total,
            spans.Any(x => x.Highlighted && x.Text.Any(c => !char.IsWhiteSpace(c))),
            spans.Any(x => x.Shaded && x.Text.Any(c => !char.IsWhiteSpace(c))));
    }

    private static IReadOnlyList<DocumentBlock> ExtractPdfBlocks(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var document = PdfDocument.Open(stream);
        var result = new List<DocumentBlock>();
        var line = 0;
        foreach (var page in document.GetPages())
        {
            var text = ContentOrderTextExtractor.GetText(page);
            foreach (var part in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
                result.Add(new(++line, part, DocumentBlockKind.PdfLine, [], false, false, false));
        }
        if (result.All(x => string.IsNullOrWhiteSpace(x.Text)))
            throw new InvalidDataException("PDF không có lớp văn bản; hãy dùng PDF có thể chọn chữ hoặc DOCX.");
        return result;
    }

    private static QuizDocumentParseResult ParseBlocks(IReadOnlyList<DocumentBlock> source)
    {
        var warnings = new List<QuizImportIssueDto>();
        var errors = new List<QuizImportIssueDto>();
        var questions = new List<QuizImportQuestion>();
        DraftQuestion? current = null;
        DraftChoice? currentChoice = null;

        void CloseCurrent()
        {
            if (current is null)
                return;
            ValidateAndAppend(current, questions, warnings, errors);
            current = null;
            currentChoice = null;
        }

        foreach (var block in source)
        {
            var line = NormalizeWhitespace(block.Text);
            if (line.Length == 0)
                continue;

            var answerMatch = AnswerLineRegex().Match(line);
            if (answerMatch.Success)
            {
                if (current is null)
                {
                    errors.Add(new(null, block.Line, "ANSWER_WITHOUT_QUESTION", "Dòng đáp án không gắn với câu hỏi nào."));
                    continue;
                }
                if (current.AnswerLine.HasValue)
                {
                    errors.Add(new(current.Number, block.Line, "DUPLICATE_ANSWER", "Câu hỏi có nhiều hơn một dòng đáp án."));
                    continue;
                }
                current.AnswerLine = block.Line;
                current.AnswerText = answerMatch.Groups["answer"].Value.Trim();
                currentChoice = null;
                continue;
            }

            var questionMatch = QuestionPrefixRegex().Match(line);
            if (questionMatch.Success)
            {
                CloseCurrent();
                current = new DraftQuestion(
                    questions.Count + 1,
                    block.Line,
                    questionMatch.Groups["text"].Value.Trim());
                continue;
            }

            var choiceMatch = ChoiceLineRegex().Match(line);
            if (choiceMatch.Success)
            {
                if (current is null)
                {
                    errors.Add(new(null, block.Line, "CHOICE_WITHOUT_QUESTION", "Lựa chọn không gắn với câu hỏi nào."));
                    continue;
                }
                if (current.AnswerLine.HasValue)
                {
                    errors.Add(new(current.Number, block.Line, "QUESTION_NOT_CLOSED", "Lựa chọn xuất hiện sau dòng đáp án của câu hiện tại."));
                    continue;
                }
                var label = choiceMatch.Groups["label"].Value.ToUpperInvariant();
                if (current.ChoicesByLabel.ContainsKey(label))
                {
                    errors.Add(new(current.Number, block.Line, "DUPLICATE_CHOICE", $"Lựa chọn {label} bị lặp."));
                    continue;
                }
                currentChoice = new(
                    label,
                    block.Line,
                    choiceMatch.Groups["text"].Value.Trim(),
                    choiceMatch.Groups["marker"].Success,
                    block.IsPredominantlyBold,
                    block.HasHighlight,
                    block.HasShading);
                current.Choices.Add(currentChoice);
                current.ChoicesByLabel[label] = currentChoice;
                continue;
            }

            if (current is null)
            {
                current = new DraftQuestion(questions.Count + 1, block.Line, line);
                continue;
            }
            if (current.AnswerLine.HasValue)
            {
                current.AnswerText = $"{current.AnswerText} {line}".Trim();
            }
            else if (current.Choices.Count == 0)
            {
                current.Text = $"{current.Text} {line}".Trim();
            }
            else if (currentChoice is not null)
            {
                currentChoice.Text = $"{currentChoice.Text} {line}".Trim();
            }
            else
            {
                errors.Add(new(current.Number, block.Line, "QUESTION_NOT_CLOSED", "Câu hỏi mới xuất hiện trước khi câu hiện tại có dòng đáp án."));
            }
        }
        CloseCurrent();

        if (questions.Count is < 1 or > 500)
        {
            errors.Add(new(null, null, "QUESTION_COUNT", "Đề phải có từ 1 đến 500 câu hỏi hợp lệ."));
        }
        else
        {
            var points = QuizScoreAllocator.Allocate(questions.Count);
            questions = questions.Select((question, index) => question with { Points = points[index] }).ToList();
        }
        return new(new(questions), warnings, errors);
    }

    private static void ValidateAndAppend(
        DraftQuestion question,
        List<QuizImportQuestion> output,
        List<QuizImportIssueDto> warnings,
        List<QuizImportIssueDto> errors)
    {
        var before = errors.Count;
        if (string.IsNullOrWhiteSpace(question.Text) || question.Text.Length > 5000)
            errors.Add(new(question.Number, question.StartLine, "QUESTION_TEXT", "Nội dung câu hỏi phải có từ 1 đến 5000 ký tự."));
        if (question.Choices.Count is < 2 or > 10)
            errors.Add(new(question.Number, question.StartLine, "CHOICE_COUNT", "Mỗi câu phải có từ 2 đến 10 lựa chọn."));
        foreach (var choice in question.Choices)
        {
            if (string.IsNullOrWhiteSpace(choice.Text) || choice.Text.Length > 5000)
                errors.Add(new(question.Number, choice.Line, "CHOICE_TEXT", $"Lựa chọn {choice.Label} không được trống và tối đa 5000 ký tự."));
        }

        var explicitLabels = new HashSet<string>(StringComparer.Ordinal);
        var explicitAvailable = question.AnswerLine.HasValue;
        var explicitValid = false;
        if (explicitAvailable)
            explicitValid = ParseExplicitAnswer(question, explicitLabels, warnings, errors);

        var markerLabels = question.Choices
            .Where(x => x.HasMarker)
            .Select(x => x.Label)
            .ToHashSet(StringComparer.Ordinal);
        var formatLabels = FormattedChoiceLabels(question.Choices);
        HashSet<string> chosen;
        if (explicitValid)
        {
            chosen = explicitLabels;
            WarnOnConflict(question, "dấu trực quan", explicitLabels, markerLabels, warnings);
            WarnOnConflict(question, "định dạng DOCX", explicitLabels, formatLabels, warnings);
        }
        else if (!explicitAvailable && markerLabels.Count > 0)
        {
            chosen = markerLabels;
            WarnOnConflict(question, "định dạng DOCX", markerLabels, formatLabels, warnings);
        }
        else if (!explicitAvailable && formatLabels.Count > 0)
        {
            chosen = formatLabels;
        }
        else
        {
            chosen = [];
            if (!explicitAvailable)
                errors.Add(new(question.Number, question.StartLine, "ANSWER_MISSING", "Không tìm thấy dòng đáp án, dấu ✓/[x]/* hoặc định dạng đáp án đúng."));
        }

        if (chosen.Count == 0 && explicitValid)
            errors.Add(new(question.Number, question.AnswerLine, "ANSWER_MISSING", "Câu hỏi phải có ít nhất một đáp án đúng."));
        foreach (var label in chosen)
        {
            if (!question.ChoicesByLabel.ContainsKey(label))
                errors.Add(new(question.Number, question.AnswerLine, "ANSWER_UNKNOWN_CHOICE", $"Đáp án {label} không tồn tại trong các lựa chọn."));
        }
        if (errors.Count != before)
            return;

        var correctIndexes = question.Choices
            .Select((choice, index) => (choice.Label, index))
            .Where(x => chosen.Contains(x.Label))
            .Select(x => x.index)
            .ToList();
        output.Add(new(
            question.Text,
            0m,
            correctIndexes.Count > 1,
            question.Choices.Select(x => x.Text).ToList(),
            correctIndexes));
    }

    private static bool ParseExplicitAnswer(
        DraftQuestion question,
        HashSet<string> labels,
        List<QuizImportIssueDto> warnings,
        List<QuizImportIssueDto> errors)
    {
        var answerText = NormalizeWhitespace(question.AnswerText ?? string.Empty);
        if (IsNoneOfAboveText(answerText))
        {
            var existing = question.Choices.FirstOrDefault(x => IsNoneOfAboveText(x.Text));
            if (existing is null)
            {
                if (question.Choices.Count >= 10)
                {
                    errors.Add(new(question.Number, question.AnswerLine, "SYNTHETIC_NONE_OPTION_LIMIT", "Không thể thêm lựa chọn 'Không có đáp án đúng' vì câu đã có 10 lựa chọn."));
                    return false;
                }
                var label = Enumerable.Range(0, 10)
                    .Select(index => ((char)('A' + index)).ToString())
                    .First(candidate => !question.ChoicesByLabel.ContainsKey(candidate));
                existing = new(label, question.AnswerLine ?? question.StartLine, SyntheticNoneChoice, false, false, false, false);
                question.Choices.Add(existing);
                question.ChoicesByLabel[label] = existing;
                warnings.Add(new(
                    question.Number,
                    question.AnswerLine,
                    "SYNTHETIC_NONE_OPTION_ADDED",
                    "Đã thêm lựa chọn 'Không có đáp án đúng trong các lựa chọn' theo dòng đáp án."));
            }
            labels.Add(existing.Label);
            return true;
        }

        var multi = MultiLabelOnlyRegex().Match(answerText);
        if (multi.Success)
        {
            foreach (Capture capture in multi.Groups["label"].Captures)
                labels.Add(capture.Value.ToUpperInvariant());
            return true;
        }

        if (answerText.Contains('|'))
        {
            var segments = answerText.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var parsed = segments.Select(value => SingleAnswerRegex().Match(value)).ToList();
            if (segments.Length > 1 && parsed.All(x => x.Success))
            {
                foreach (var match in parsed)
                    AddExplicitAnswerPart(question, match, labels, warnings);
                return true;
            }
        }

        var single = SingleAnswerRegex().Match(answerText);
        if (single.Success)
        {
            AddExplicitAnswerPart(question, single, labels, warnings);
            return true;
        }

        errors.Add(new(
            question.Number,
            question.AnswerLine,
            "ANSWER_FORMAT",
            $"Không đọc được đáp án '{answerText}'. Dùng A, A;C, A và C hoặc A. Nội dung."));
        return false;
    }

    private static void AddExplicitAnswerPart(
        DraftQuestion question,
        Match match,
        HashSet<string> labels,
        List<QuizImportIssueDto> warnings)
    {
        var label = match.Groups["label"].Value.ToUpperInvariant();
        labels.Add(label);
        var expectedText = match.Groups["text"].Value.Trim();
        if (expectedText.Length == 0 || !question.ChoicesByLabel.TryGetValue(label, out var choice))
            return;
        if (!NormalizeForComparison(choice.Text).Equals(
                NormalizeForComparison(expectedText),
                StringComparison.Ordinal))
        {
            warnings.Add(new(
                question.Number,
                question.AnswerLine,
                "ANSWER_TEXT_DIFFERENT",
                $"Nhãn đáp án {label} hợp lệ nhưng phần mô tả khác nhẹ nội dung lựa chọn; hệ thống dùng nhãn {label}."));
        }
    }

    private static HashSet<string> FormattedChoiceLabels(IReadOnlyList<DraftChoice> choices)
    {
        if (choices.Count == 0)
            return [];
        var allBold = choices.All(x => x.IsPredominantlyBold);
        var allHighlighted = choices.All(x => x.HasHighlight);
        var allShaded = choices.All(x => x.HasShading);
        return choices
            .Where(x =>
                (!allBold && x.IsPredominantlyBold)
                || (!allHighlighted && x.HasHighlight)
                || (!allShaded && x.HasShading))
            .Select(x => x.Label)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static void WarnOnConflict(
        DraftQuestion question,
        string secondarySource,
        HashSet<string> primary,
        HashSet<string> secondary,
        List<QuizImportIssueDto> warnings)
    {
        if (secondary.Count == 0 || primary.SetEquals(secondary))
            return;
        warnings.Add(new(
            question.Number,
            question.AnswerLine ?? question.StartLine,
            "ANSWER_SIGNAL_CONFLICT",
            $"Nguồn {secondarySource} không khớp nguồn đáp án ưu tiên; hệ thống giữ {string.Join(", ", primary.Order())}."));
    }

    private static bool IsNoneOfAboveText(string value) =>
        NoneOfAboveRegex().IsMatch(NormalizeForComparison(value));

    private static string NormalizeWhitespace(string value) =>
        WhitespaceRegex().Replace(value.Normalize(NormalizationForm.FormKC), " ").Trim();

    private static string NormalizeForComparison(string value)
    {
        var normalized = NormalizeWhitespace(value)
            .Replace('\u00A0', ' ')
            .Replace('“', '"')
            .Replace('”', '"')
            .Replace('‘', '\'')
            .Replace('’', '\'')
            .Replace('–', '-')
            .Replace('—', '-')
            .Trim();
        return TerminalPunctuationRegex().Replace(normalized, string.Empty).Trim().ToUpperInvariant();
    }

    [GeneratedRegex(@"^\s*(?:Đáp\s*án\s*đúng|Dap\s*an\s*dung|Đáp\s*án|Answer)\s*:\s*(?<answer>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnswerLineRegex();

    [GeneratedRegex(@"^\s*(?:(?<marker>✓|\[x\]|\*)\s*)?(?<label>[A-J])\s*(?:[\.\):]|-\s+)\s*(?<text>.*?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChoiceLineRegex();

    [GeneratedRegex(@"^\s*(?<label>[A-J])(?:\s*(?:,|;|\||và|and)\s*(?<label>[A-J]))+\s*[\.\)]?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MultiLabelOnlyRegex();

    [GeneratedRegex(@"^\s*(?<label>[A-J])(?:\s*[\.\)]\s*(?<text>.*))?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SingleAnswerRegex();

    [GeneratedRegex(@"^\s*(?:\d+(?:[_\.]\d+)*|Câu\s+\d+|Question\s+\d+)\s*[:\.\)]\s*(?<text>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuestionPrefixRegex();

    [GeneratedRegex(@"^(?:KHÔNG CÓ ĐÁP ÁN ĐÚNG TRONG CÁC LỰA CHỌN|KHÔNG CÓ LỰA CHỌN NÀO ĐÚNG|NONE OF THE ABOVE CHOICES IS CORRECT)(?:\b|\s|[.,;:])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoneOfAboveRegex();

    [GeneratedRegex(@"[\s\u00A0]+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[\s\.,;:!?]+$")]
    private static partial Regex TerminalPunctuationRegex();

    private sealed class DraftQuestion(int number, int startLine, string text)
    {
        public int Number { get; } = number;
        public int StartLine { get; } = startLine;
        public string Text { get; set; } = text;
        public List<DraftChoice> Choices { get; } = [];
        public Dictionary<string, DraftChoice> ChoicesByLabel { get; } = new(StringComparer.Ordinal);
        public int? AnswerLine { get; set; }
        public string? AnswerText { get; set; }
    }

    private sealed class DraftChoice(
        string label,
        int line,
        string text,
        bool hasMarker,
        bool isPredominantlyBold,
        bool hasHighlight,
        bool hasShading)
    {
        public string Label { get; } = label;
        public int Line { get; } = line;
        public string Text { get; set; } = text;
        public bool HasMarker { get; } = hasMarker;
        public bool IsPredominantlyBold { get; } = isPredominantlyBold;
        public bool HasHighlight { get; } = hasHighlight;
        public bool HasShading { get; } = hasShading;
    }

    private readonly record struct TextFormat(bool Bold, bool Highlighted, bool Shaded)
    {
        public TextFormat Apply(PartialTextFormat other) => new(
            other.Bold ?? Bold,
            other.Highlighted ?? Highlighted,
            other.Shaded ?? Shaded);
    }

    private readonly record struct PartialTextFormat(bool? Bold, bool? Highlighted, bool? Shaded)
    {
        public PartialTextFormat Apply(PartialTextFormat other) => new(
            other.Bold ?? Bold,
            other.Highlighted ?? Highlighted,
            other.Shaded ?? Shaded);
    }

    private sealed record StyleDefinition(string? BasedOn, PartialTextFormat Format);

    private sealed class DocxStyleResolver
    {
        private readonly Dictionary<string, StyleDefinition> definitions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PartialTextFormat> resolved = new(StringComparer.Ordinal);

        public DocxStyleResolver(ZipArchive archive)
        {
            var entry = archive.GetEntry("word/styles.xml");
            if (entry is null)
                return;
            XDocument document;
            using (var stream = entry.Open())
                document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            foreach (var style in document.Descendants().Where(x => x.Name.LocalName == "style"))
            {
                var id = Attribute(style, "styleId");
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                var basedOn = style.Elements().FirstOrDefault(x => x.Name.LocalName == "basedOn");
                var runProperties = style.Elements().FirstOrDefault(x => x.Name.LocalName == "rPr");
                definitions[id] = new(
                    basedOn is null ? null : Attribute(basedOn, "val"),
                    ReadFormat(runProperties));
            }
        }

        public TextFormat ResolveParagraph(XElement paragraph)
        {
            var properties = paragraph.Elements().FirstOrDefault(x => x.Name.LocalName == "pPr");
            var styleElement = properties?.Elements().FirstOrDefault(x => x.Name.LocalName == "pStyle");
            var style = styleElement is null
                ? default
                : ResolveStyle(Attribute(styleElement, "val"), []);
            var direct = ReadFormat(properties?.Elements().FirstOrDefault(x => x.Name.LocalName == "rPr"));
            var merged = style.Apply(direct);
            return new(merged.Bold ?? false, merged.Highlighted ?? false, merged.Shaded ?? false);
        }

        public TextFormat ResolveRun(XElement run, TextFormat paragraph)
        {
            var properties = run.Elements().FirstOrDefault(x => x.Name.LocalName == "rPr");
            var styleElement = properties?.Elements().FirstOrDefault(x => x.Name.LocalName == "rStyle");
            var style = styleElement is null
                ? default
                : ResolveStyle(Attribute(styleElement, "val"), []);
            return paragraph.Apply(style).Apply(ReadFormat(properties));
        }

        private PartialTextFormat ResolveStyle(string? id, HashSet<string> stack)
        {
            if (string.IsNullOrWhiteSpace(id))
                return default;
            if (resolved.TryGetValue(id, out var cached))
                return cached;
            if (!definitions.TryGetValue(id, out var definition) || !stack.Add(id))
                return default;
            var value = ResolveStyle(definition.BasedOn, stack).Apply(definition.Format);
            stack.Remove(id);
            resolved[id] = value;
            return value;
        }

        private static PartialTextFormat ReadFormat(XElement? properties)
        {
            if (properties is null)
                return default;
            var bold = properties.Elements().FirstOrDefault(x => x.Name.LocalName is "b" or "bCs");
            var highlight = properties.Elements().FirstOrDefault(x => x.Name.LocalName == "highlight");
            var shading = properties.Elements().FirstOrDefault(x => x.Name.LocalName == "shd");
            return new(
                bold is null ? null : ToggleValue(bold),
                highlight is null ? null : MeaningfulColor(Attribute(highlight, "val")),
                shading is null ? null : MeaningfulShading(shading));
        }

        private static bool ToggleValue(XElement element)
        {
            var value = Attribute(element, "val");
            return value is null
                || !value.Equals("false", StringComparison.OrdinalIgnoreCase)
                && !value.Equals("0", StringComparison.OrdinalIgnoreCase)
                && !value.Equals("off", StringComparison.OrdinalIgnoreCase)
                && !value.Equals("no", StringComparison.OrdinalIgnoreCase);
        }

        private static bool MeaningfulColor(string? value) =>
            !string.IsNullOrWhiteSpace(value)
            && !value.Equals("none", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("auto", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("white", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("FFFFFF", StringComparison.OrdinalIgnoreCase);

        private static bool MeaningfulShading(XElement element) =>
            MeaningfulColor(Attribute(element, "fill"))
            || !string.IsNullOrWhiteSpace(Attribute(element, "themeFill"));

        private static string? Attribute(XElement element, string name) =>
            element.Attributes().FirstOrDefault(x => x.Name.LocalName == name)?.Value;
    }
}
