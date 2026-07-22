using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.Infrastructure.Services;

public sealed class QuizService(AppDbContext db, IOutboxService outbox) : IQuizService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan OfflineSyncGrace = TimeSpan.FromMinutes(15);

    public async Task<QuizImportResultDto> ImportAsync(Guid examId, QuizImportFileRequest request, CancellationToken cancellationToken)
    {
        var exam = await db.ExamsSet.Include(x => x.QuizQuestions).ThenInclude(x => x.Choices)
            .FirstOrDefaultAsync(x => x.Id == examId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy đề thi.", 404);
        if (exam.Status != ExamStatus.Draft)
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Chỉ được nhập câu hỏi khi đề đang ở trạng thái nháp.", 409);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(request.Base64Content); }
        catch { throw new ApiException(ErrorCodes.ValidationFailed, "Nội dung tệp không phải Base64 hợp lệ."); }
        if (bytes.Length == 0 || bytes.Length > 10 * 1024 * 1024)
            throw new ApiException(ErrorCodes.ValidationFailed, "Tệp câu hỏi phải có dung lượng từ 1 byte đến 10 MB.");

        var document = ParseDocument(request.FileName, bytes);
        Validate(document);
        var replacedQuestions = exam.QuizQuestions.Where(x => x.Version == exam.Version).ToList();
        var replacedChoices = replacedQuestions.SelectMany(x => x.Choices).ToList();
        db.QuizQuestionsSet.RemoveRange(replacedQuestions);
        var order = 0;
        foreach (var input in document.Questions)
        {
            var question = new QuizQuestion
            {
                ExamId = exam.Id, Version = exam.Version, Order = ++order, Text = input.Text.Trim(),
                Points = input.Points, Multiple = input.Multiple
            };
            for (var index = 0; index < input.Choices.Count; index++)
                question.Choices.Add(new QuizChoice { Order = index + 1, Text = input.Choices[index].Trim(), IsCorrect = input.CorrectChoiceIndexes.Contains(index) });
            db.QuizQuestionsSet.Add(question);
        }
        exam.DeliveryType = ExamDeliveryType.MultipleChoice;
        await db.SaveChangesAsync(cancellationToken);
        foreach (var choice in replacedChoices) await outbox.EnqueueAsync("quiz_choices", choice.Id.ToString(), "delete", new { id = choice.Id }, cancellationToken: cancellationToken);
        foreach (var question in replacedQuestions) await outbox.EnqueueAsync("quiz_questions", question.Id.ToString(), "delete", new { id = question.Id }, cancellationToken: cancellationToken);
        foreach (var question in await db.QuizQuestionsSet.AsNoTracking().Include(x => x.Choices).Where(x => x.ExamId == exam.Id && x.Version == exam.Version).ToListAsync(cancellationToken))
        {
            await outbox.EnqueueAsync("quiz_questions", question.Id.ToString(), "upsert", QuestionCloud(question), cancellationToken: cancellationToken);
            foreach (var choice in question.Choices) await outbox.EnqueueAsync("quiz_choices", choice.Id.ToString(), "upsert", ChoiceCloud(choice), cancellationToken: cancellationToken);
        }
        return new(exam.Id, exam.Version, document.Questions.Count, document.Questions.Sum(x => x.Points));
    }

    public async Task<QuizAttemptDto> StartOrGetAttemptAsync(Guid sessionId, Guid participantId, CancellationToken cancellationToken)
    {
        var existing = await db.QuizAttemptsSet.Include(x => x.Answers)
            .FirstOrDefaultAsync(x => x.SessionId == sessionId && x.ParticipantId == participantId, cancellationToken);
        if (existing is not null) return ToDto(existing);

        var participant = await db.SessionParticipantsSet.AsNoTracking()
            .Include(x => x.Session).ThenInclude(x => x.Exam).ThenInclude(x => x.QuizQuestions).ThenInclude(x => x.Choices)
            .FirstOrDefaultAsync(x => x.Id == participantId && x.SessionId == sessionId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy lượt dự thi.", 404);
        var session = participant.Session;
        if (participant.Status != ParticipantStatus.Approved)
            throw new ApiException(ErrorCodes.Forbidden, "Lượt dự thi chưa được duyệt.", 403);
        if (session.Status is not (SessionStatus.InProgress or SessionStatus.Paused))
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Bài trắc nghiệm chưa bắt đầu.", 409);
        if (session.Exam.DeliveryType != ExamDeliveryType.MultipleChoice)
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Đề này không phải đề trắc nghiệm.", 409);
        var questions = session.Exam.QuizQuestions.Where(x => x.Version == session.Exam.Version).OrderBy(x => x.Order).Select(ToQuestionDto).ToList();
        if (questions.Count == 0) throw new ApiException(ErrorCodes.InvalidStateTransition, "Đề chưa có câu hỏi trắc nghiệm.", 409);
        var deadline = session.StartedAtUtc!.Value.AddMinutes(session.Exam.DurationMinutes + participant.ExtraTimeMinutes);
        if (DateTimeOffset.UtcNow > deadline) throw new ApiException(ErrorCodes.DeadlinePassed, "Đã hết thời gian làm bài.", 409);
        var attempt = new QuizAttempt
        {
            SessionId = sessionId, ParticipantId = participantId, ExamVersion = session.Exam.Version,
            StartedAtUtc = DateTimeOffset.UtcNow, DeadlineUtc = deadline,
            MaxScore = questions.Sum(x => x.Points), SnapshotJson = JsonSerializer.Serialize(questions, Json)
        };
        db.QuizAttemptsSet.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);
        await outbox.EnqueueAsync("quiz_attempts", attempt.Id.ToString(), "upsert", AttemptCloud(attempt), cancellationToken: cancellationToken);
        return ToDto(attempt);
    }

    public async Task<SyncQuizAnswersResultDto> SyncAnswersAsync(Guid attemptId, Guid participantId, SyncQuizAnswersRequest request, CancellationToken cancellationToken)
    {
        var attempt = await OwnedAttempt(attemptId, participantId, cancellationToken);
        if (attempt.Status == QuizAttemptStatus.Finalized)
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Bài đã chốt nên không thể sửa đáp án.", 409);
        if (DateTimeOffset.UtcNow > attempt.DeadlineUtc + OfflineSyncGrace
            || request.Answers.Any(x => x.ClientUpdatedAtUtc > attempt.DeadlineUtc))
            throw new ApiException(ErrorCodes.DeadlinePassed, "Đáp án được tạo sau deadline hoặc đã quá thời gian đồng bộ ngoại tuyến.", 409);
        var questions = Snapshot(attempt);
        var byId = questions.ToDictionary(x => x.Id);
        foreach (var incoming in request.Answers)
        {
            if (!byId.TryGetValue(incoming.QuestionId, out var question))
                throw new ApiException(ErrorCodes.ValidationFailed, "Đáp án chứa câu hỏi không thuộc đề đã chụp.");
            var selected = incoming.ChoiceIds.Distinct().ToList();
            if (selected.Any(id => question.Choices.All(x => x.Id != id)) || (!question.Multiple && selected.Count > 1))
                throw new ApiException(ErrorCodes.ValidationFailed, "Lựa chọn không hợp lệ cho câu hỏi.");
            var answer = attempt.Answers.FirstOrDefault(x => x.QuestionId == incoming.QuestionId);
            if (answer is not null && incoming.Revision <= answer.Revision) continue;
            if (answer is null)
            {
                answer = new QuizAnswer { AttemptId = attempt.Id, QuestionId = incoming.QuestionId };
                db.QuizAnswersSet.Add(answer);
            }
            answer.ChoiceIdsJson = JsonSerializer.Serialize(selected, Json);
            answer.Revision = incoming.Revision;
            answer.ClientUpdatedAtUtc = incoming.ClientUpdatedAtUtc;
        }
        await db.SaveChangesAsync(cancellationToken);
        foreach (var answer in attempt.Answers) await outbox.EnqueueAsync("quiz_answers", answer.Id.ToString(), "upsert", AnswerCloud(answer), cancellationToken: cancellationToken);
        return new(attempt.Id, attempt.Answers.Select(ToAnswerDto).ToList(), DateTimeOffset.UtcNow);
    }

    public async Task<QuizAttemptDto> FinalizeAsync(Guid attemptId, Guid participantId, FinalizeQuizAttemptRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 100)
            throw new ApiException(ErrorCodes.ValidationFailed, "Idempotency key không hợp lệ.");
        var attempt = await OwnedAttempt(attemptId, participantId, cancellationToken);
        if (attempt.Status == QuizAttemptStatus.Finalized) return ToDto(attempt);
        var questionIds = Snapshot(attempt).Select(x => x.Id).ToList();
        var questions = await db.QuizQuestionsSet.AsNoTracking().Include(x => x.Choices)
            .Where(x => questionIds.Contains(x.Id)).ToListAsync(cancellationToken);
        decimal score = 0;
        foreach (var question in questions)
        {
            var expected = question.Choices.Where(x => x.IsCorrect).Select(x => x.Id).Order().ToArray();
            var answer = attempt.Answers.FirstOrDefault(x => x.QuestionId == question.Id);
            var actual = answer is null ? [] : JsonSerializer.Deserialize<List<Guid>>(answer.ChoiceIdsJson, Json)!.Distinct().Order().ToArray();
            if (expected.SequenceEqual(actual)) score += question.Points;
        }
        attempt.Score = score;
        attempt.Status = QuizAttemptStatus.Finalized;
        attempt.FinalizedAtUtc = DateTimeOffset.UtcNow;
        attempt.FinalizeIdempotencyKey = request.IdempotencyKey.Trim();
        await db.SaveChangesAsync(cancellationToken);
        await outbox.EnqueueAsync("quiz_attempts", attempt.Id.ToString(), "upsert", AttemptCloud(attempt), cancellationToken: cancellationToken);
        return ToDto(attempt);
    }

    private async Task<QuizAttempt> OwnedAttempt(Guid attemptId, Guid participantId, CancellationToken ct) =>
        await db.QuizAttemptsSet.Include(x => x.Answers).FirstOrDefaultAsync(x => x.Id == attemptId && x.ParticipantId == participantId, ct)
        ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy bài làm trắc nghiệm.", 404);

    private static QuizAttemptDto ToDto(QuizAttempt attempt) => new(
        attempt.Id, attempt.SessionId, attempt.ParticipantId, attempt.Status, attempt.ExamVersion,
        attempt.StartedAtUtc, attempt.DeadlineUtc, attempt.FinalizedAtUtc, attempt.Score, attempt.MaxScore,
        Snapshot(attempt), attempt.Answers.Select(ToAnswerDto).ToList());
    private static QuizAnswerDto ToAnswerDto(QuizAnswer x) => new(x.QuestionId, JsonSerializer.Deserialize<List<Guid>>(x.ChoiceIdsJson, Json) ?? [], x.Revision, x.ClientUpdatedAtUtc);
    private static IReadOnlyList<QuizQuestionDto> Snapshot(QuizAttempt x) => JsonSerializer.Deserialize<List<QuizQuestionDto>>(x.SnapshotJson, Json) ?? [];
    private static QuizQuestionDto ToQuestionDto(QuizQuestion x) => new(x.Id, x.Text, x.Order, x.Points, x.Multiple, x.Choices.OrderBy(c => c.Order).Select(c => new QuizChoiceDto(c.Id, c.Text, c.Order)).ToList());
    private static object QuestionCloud(QuizQuestion x) => new { id = x.Id, exam_id = x.ExamId, version = x.Version, sort_order = x.Order, question_text = x.Text, points = x.Points, multiple = x.Multiple, created_at = x.CreatedAtUtc, updated_at = x.UpdatedAtUtc };
    private static object ChoiceCloud(QuizChoice x) => new { id = x.Id, question_id = x.QuestionId, sort_order = x.Order, choice_text = x.Text, is_correct = x.IsCorrect, created_at = x.CreatedAtUtc, updated_at = x.UpdatedAtUtc };
    private static object AttemptCloud(QuizAttempt x) => new { id = x.Id, session_id = x.SessionId, participant_id = x.ParticipantId, exam_version = x.ExamVersion, status = x.Status.ToString(), started_at = x.StartedAtUtc, deadline_at = x.DeadlineUtc, finalized_at = x.FinalizedAtUtc, score = x.Score, max_score = x.MaxScore, snapshot_json = x.SnapshotJson, finalize_idempotency_key = x.FinalizeIdempotencyKey, created_at = x.CreatedAtUtc, updated_at = x.UpdatedAtUtc };
    private static object AnswerCloud(QuizAnswer x) => new { id = x.Id, attempt_id = x.AttemptId, question_id = x.QuestionId, choice_ids = x.ChoiceIdsJson, revision = x.Revision, client_updated_at = x.ClientUpdatedAtUtc, created_at = x.CreatedAtUtc, updated_at = x.UpdatedAtUtc };

    private static QuizImportDocument ParseDocument(string fileName, byte[] bytes)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        try
        {
            return extension switch
            {
                ".json" => JsonSerializer.Deserialize<QuizImportDocument>(bytes, Json) ?? throw new InvalidDataException(),
                ".csv" => FromRows(ParseCsv(Encoding.UTF8.GetString(bytes))),
                ".xlsx" => FromRows(ParseXlsx(bytes)),
                _ => throw new ApiException(ErrorCodes.ValidationFailed, "Chỉ hỗ trợ tệp JSON, CSV hoặc XLSX có cấu trúc chính thức.")
            };
        }
        catch (ApiException) { throw; }
        catch (Exception ex) { throw new ApiException(ErrorCodes.ValidationFailed, "Không đọc được cấu trúc tệp câu hỏi.", details: ex.Message); }
    }

    private static void Validate(QuizImportDocument document)
    {
        if (document.Questions.Count is < 1 or > 500) throw new ApiException(ErrorCodes.ValidationFailed, "Đề phải có từ 1 đến 500 câu hỏi.");
        foreach (var q in document.Questions)
        {
            if (string.IsNullOrWhiteSpace(q.Text) || q.Text.Length > 5000 || q.Points <= 0 || q.Points > 100)
                throw new ApiException(ErrorCodes.ValidationFailed, "Nội dung hoặc điểm câu hỏi không hợp lệ.");
            if (q.Choices.Count is < 2 or > 10 || q.Choices.Any(string.IsNullOrWhiteSpace))
                throw new ApiException(ErrorCodes.ValidationFailed, "Mỗi câu phải có từ 2 đến 10 lựa chọn.");
            var correct = q.CorrectChoiceIndexes.Distinct().ToList();
            if (correct.Count == 0 || correct.Any(x => x < 0 || x >= q.Choices.Count) || (!q.Multiple && correct.Count != 1))
                throw new ApiException(ErrorCodes.ValidationFailed, "Đáp án đúng không hợp lệ.");
        }
    }

    private static QuizImportDocument FromRows(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count < 2) throw new InvalidDataException();
        var headers = rows[0].Select((x, i) => (x.Trim().ToLowerInvariant(), i)).ToDictionary(x => x.Item1, x => x.i);
        string Cell(IReadOnlyList<string> row, string name) => headers.TryGetValue(name, out var i) && i < row.Count ? row[i].Trim() : string.Empty;
        var result = new List<QuizImportQuestion>();
        foreach (var row in rows.Skip(1).Where(x => x.Any(v => !string.IsNullOrWhiteSpace(v))))
        {
            var choices = new[] { "choice_a", "choice_b", "choice_c", "choice_d", "choice_e", "choice_f", "choice_g", "choice_h", "choice_i", "choice_j" }.Select(x => Cell(row, x)).TakeWhile(x => !string.IsNullOrWhiteSpace(x)).ToList();
            var correct = Cell(row, "correct").Split(['|', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => char.IsLetter(x[0]) ? char.ToUpperInvariant(x[0]) - 'A' : int.Parse(x, CultureInfo.InvariantCulture) - 1).ToList();
            result.Add(new(Cell(row, "question"), decimal.Parse(Cell(row, "points"), CultureInfo.InvariantCulture), bool.TryParse(Cell(row, "multiple"), out var multiple) ? multiple : correct.Count > 1, choices, correct));
        }
        return new(result);
    }

    private static IReadOnlyList<IReadOnlyList<string>> ParseCsv(string text)
    {
        var rows = new List<IReadOnlyList<string>>(); var row = new List<string>(); var cell = new StringBuilder(); var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' && quoted && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
            else if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted) { row.Add(cell.ToString()); cell.Clear(); }
            else if ((c == '\n' || c == '\r') && !quoted) { if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = []; }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    private static IReadOnlyList<IReadOnlyList<string>> ParseXlsx(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes); using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Sum(x => x.Length) > 30L * 1024 * 1024 || zip.Entries.Any(x => x.Length > 20L * 1024 * 1024))
            throw new InvalidDataException("XLSX giải nén vượt giới hạn an toàn.");
        var shared = zip.GetEntry("xl/sharedStrings.xml") is { } stringsEntry
            ? XDocument.Load(stringsEntry.Open()).Descendants().Where(x => x.Name.LocalName == "si").Select(x => string.Concat(x.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value))).ToList()
            : [];
        var sheet = zip.GetEntry("xl/worksheets/sheet1.xml") ?? throw new InvalidDataException();
        var document = XDocument.Load(sheet.Open()); var rows = new List<IReadOnlyList<string>>();
        foreach (var row in document.Descendants().Where(x => x.Name.LocalName == "row"))
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements().Where(x => x.Name.LocalName == "c"))
            {
                var reference = cell.Attribute("r")?.Value ?? "A1"; var column = reference.TakeWhile(char.IsLetter).Aggregate(0, (n, c) => n * 26 + char.ToUpperInvariant(c) - 'A' + 1) - 1;
                while (cells.Count <= column) cells.Add(string.Empty);
                var raw = cell.Descendants().FirstOrDefault(x => x.Name.LocalName is "v" or "t")?.Value ?? string.Empty;
                cells[column] = cell.Attribute("t")?.Value == "s" && int.TryParse(raw, out var index) ? shared[index] : raw;
            }
            rows.Add(cells);
        }
        return rows;
    }
}
