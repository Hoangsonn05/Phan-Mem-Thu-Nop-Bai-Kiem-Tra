using System.Text;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Storage;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.Infrastructure.Services;

public sealed class GradeService(AppDbContext db, IStoragePaths paths, IChunkStorage chunks, IAuditService audit, IOutboxService outbox, IRealtimePublisher realtime) : IGradeService
{
    public async Task<PagedResult<SubmissionSummaryDto>> GetQueueAsync(GradingStatus? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 200);
        var query = db.SubmissionsSet.AsNoTracking().Include(x => x.Files).Include(x => x.Participant)
            .Where(x => x.IsOfficial && (x.Status == SubmissionStatus.Submitted || x.Status == SubmissionStatus.LateSubmitted));
        if (status.HasValue)
        {
            query = status.Value switch
            {
                GradingStatus.NotGraded => query.Where(x => !db.GradesSet.Any(g => g.SubmissionId == x.Id)),
                _ => query.Where(x => db.GradesSet.Any(g => g.SubmissionId == x.Id && g.Status == status.Value))
            };
        }
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(x => x.Participant.StudentCode).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var items = rows.Select(s => new SubmissionSummaryDto(s.Id, s.SessionId, s.ParticipantId, s.Participant.StudentCode, s.Participant.DisplayName, s.AttemptNumber, s.Status, s.ClientSubmittedAtUtc, s.ServerReceivedAtUtc, s.DeadlineUtc, s.IsLate, s.ReceiptCode, s.IsOfficial, s.Files.Select(f => f.ToDto([])).ToList())).ToList();
        return new(items, page, pageSize, total);
    }

    public async Task<GradeDto> GetAsync(Guid submissionId, CancellationToken cancellationToken)
    {
        _ = await db.SubmissionsSet.AsNoTracking().FirstOrDefaultAsync(x => x.Id == submissionId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy bài nộp.", 404);
        var grade = await db.GradesSet.AsNoTracking().Include(x => x.RubricScores).Include(x => x.Attachments).FirstOrDefaultAsync(x => x.SubmissionId == submissionId, cancellationToken);
        return grade is null ? Empty(submissionId) : ToDto(grade);
    }

    public async Task<GradeDto> SaveAsync(Guid submissionId, SaveGradeRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        _ = await db.SubmissionsSet.FirstOrDefaultAsync(x => x.Id == submissionId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy bài nộp.", 404);
        var grade = await db.GradesSet.Include(x => x.RubricScores).Include(x => x.Attachments).FirstOrDefaultAsync(x => x.SubmissionId == submissionId, cancellationToken);
        if (grade is null)
        {
            grade = new Grade { SubmissionId = submissionId, Status = GradingStatus.InProgress, MaxScore = request.MaxScore };
            db.GradesSet.Add(grade);
        }
        else if (!string.Equals(grade.RowVersion, request.RowVersion, StringComparison.Ordinal) && request.RowVersion != "new")
            throw new ApiException(ErrorCodes.ConcurrencyConflict, "Điểm đã được cập nhật ở nơi khác.", 409, details: new { current = ToDto(grade) });
        if (grade.Status == GradingStatus.Returned) throw new ApiException(ErrorCodes.InvalidStateTransition, "Kết quả đã trả; cần reopen trước khi sửa.", 409);
        var before = grade.Id == Guid.Empty ? null : ToDto(grade);
        grade.Score = request.Score; grade.MaxScore = request.MaxScore; grade.GeneralComment = request.GeneralComment?.Trim(); grade.Status = GradingStatus.Graded; grade.GradedAtUtc = DateTimeOffset.UtcNow;
        db.RubricScoresSet.RemoveRange(grade.RubricScores);
        grade.RubricScores = request.RubricScores.Select(x => new RubricScore { GradeId = grade.Id, CriterionKey = x.CriterionKey, Title = x.Title, Score = x.Score, MaxScore = x.MaxScore, Comment = x.Comment, Order = x.Order }).ToList();
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("GradeSaved", nameof(Grade), grade.Id.ToString(), null, before, ToDto(grade), cancellationToken);
        await outbox.EnqueueAsync("grades", grade.Id.ToString(), "upsert", ToCloud(grade), cancellationToken: cancellationToken);
        foreach (var rubric in grade.RubricScores)
        {
            await outbox.EnqueueAsync(
                "rubric_scores",
                rubric.Id.ToString(),
                "upsert",
                ToCloud(rubric),
                cancellationToken: cancellationToken);
        }

        return ToDto(grade);
    }

    public async Task<GradeDto> ReturnAsync(Guid submissionId, ReturnGradeRequest request, CancellationToken cancellationToken)
    {
        var grade = await db.GradesSet.Include(x => x.RubricScores).Include(x => x.Attachments).Include(x => x.Submission).FirstOrDefaultAsync(x => x.SubmissionId == submissionId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Chưa có kết quả chấm.", 404);
        if (grade.Score is null || grade.Score < 0 || grade.Score > grade.MaxScore) throw new ApiException(ErrorCodes.ValidationFailed, "Điểm không hợp lệ để trả kết quả.");
        grade.Status = GradingStatus.Returned; grade.ReturnedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("GradeReturned", nameof(Grade), grade.Id.ToString(), grade.Submission.SessionId, null, new { grade.Score, grade.MaxScore, request.Message }, cancellationToken);
        await outbox.EnqueueAsync("grades", grade.Id.ToString(), "upsert", ToCloud(grade), cancellationToken: cancellationToken);
        var session = await db.ExamSessionsSet.FirstAsync(x => x.Id == grade.Submission.SessionId, cancellationToken); session.Sequence++; await db.SaveChangesAsync(cancellationToken);
        await realtime.PublishParticipantAsync(grade.Submission.SessionId, grade.Submission.ParticipantId, RealtimeEvents.GradeReturned, session.Sequence, new GradeReturnedEvent(submissionId, grade.Score, grade.MaxScore), cancellationToken);
        return ToDto(grade);
    }

    public async Task<GradeDto> ReopenAsync(Guid submissionId, ReopenGradeRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new ApiException(ErrorCodes.ValidationFailed, "Phải có lý do mở lại điểm.");
        var grade = await db.GradesSet.Include(x => x.RubricScores).Include(x => x.Attachments).FirstOrDefaultAsync(x => x.SubmissionId == submissionId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy kết quả chấm.", 404);
        if (grade.Status != GradingStatus.Returned) throw new ApiException(ErrorCodes.InvalidStateTransition, "Chỉ mở lại kết quả đã trả.", 409);
        var before = ToDto(grade); grade.Status = GradingStatus.InProgress; grade.ReturnedAtUtc = null; await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("GradeReopened", nameof(Grade), grade.Id.ToString(), null, before, new { grade = ToDto(grade), request.Reason }, cancellationToken);
        await outbox.EnqueueAsync(
            "grades",
            grade.Id.ToString(),
            "upsert",
            ToCloud(grade),
            cancellationToken: cancellationToken);
        return ToDto(grade);
    }

    public async Task<FileDescriptorDto> AddAttachmentAsync(Guid submissionId, string fileName, string mimeType, Stream content, long contentLength, CancellationToken cancellationToken)
    {
        if (contentLength <= 0 || contentLength > 100L * 1024 * 1024) throw new ApiException(ErrorCodes.FileTooLarge, "File đính kèm không hợp lệ hoặc quá lớn.");
        var grade = await db.GradesSet.Include(x => x.Attachments).Include(x => x.Submission).FirstOrDefaultAsync(x => x.SubmissionId == submissionId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Chưa có bản ghi chấm bài.", 404);
        var id = Guid.NewGuid(); var stored = id.ToString("N") + Path.GetExtension(fileName).ToLowerInvariant();
        var root = Path.Combine(paths.SessionRoot(grade.Submission.SessionId), "graded", submissionId.ToString("N")); Directory.CreateDirectory(root);
        var path = Path.Combine(root, stored);
        await using (var output = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true)) await content.CopyToAsync(output, cancellationToken);
        File.Move(path + ".tmp", path);
        var hash = await chunks.ComputeSha256Async(path, cancellationToken);
        var entity = new GradedAttachment { Id = id, GradeId = grade.Id, OriginalName = Path.GetFileName(fileName), StoredName = stored, RelativePath = Path.GetRelativePath(paths.RootPath, path), SizeBytes = new FileInfo(path).Length, Sha256 = hash, MimeType = mimeType };
        db.GradedAttachmentsSet.Add(entity); await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            "GradedAttachmentAdded",
            nameof(GradedAttachment),
            entity.Id.ToString(),
            grade.Submission.SessionId,
            null,
            new { entity.OriginalName, entity.SizeBytes, entity.Sha256 },
            cancellationToken);
        await outbox.EnqueueAsync(
            "graded_attachments",
            entity.Id.ToString(),
            "upsert",
            ToCloud(entity),
            path,
            cancellationToken);
        return new FileDescriptorDto(entity.Id, entity.OriginalName, entity.SizeBytes, entity.Sha256, entity.MimeType, $"/api/v1/grading/submissions/{submissionId}/attachments/{entity.Id}/content");
    }

    public async Task<byte[]> ExportGradebookCsvAsync(Guid? sessionId, CancellationToken cancellationToken)
    {
        var query = db.GradesSet.AsNoTracking().Include(x => x.Submission).ThenInclude(x => x.Participant).AsQueryable();
        if (sessionId.HasValue) query = query.Where(x => x.Submission.SessionId == sessionId.Value);
        var grades = await query.OrderBy(x => x.Submission.Participant.StudentCode).ToListAsync(cancellationToken);
        var sb = new StringBuilder("studentCode,displayName,score,maxScore,status,gradedAtUtc,returnedAtUtc\n");
        foreach (var g in grades) sb.AppendLine($"{E(g.Submission.Participant.StudentCode)},{E(g.Submission.Participant.DisplayName)},{g.Score},{g.MaxScore},{g.Status},{g.GradedAtUtc:O},{g.ReturnedAtUtc:O}");
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private static void Validate(SaveGradeRequest r)
    {
        if (r.MaxScore <= 0 || r.Score < 0 || r.Score > r.MaxScore) throw new ApiException(ErrorCodes.ValidationFailed, "Điểm phải nằm trong khoảng 0 đến điểm tối đa.");
        if (r.RubricScores.Any(x => x.Score < 0 || x.Score > x.MaxScore)) throw new ApiException(ErrorCodes.ValidationFailed, "Điểm rubric không hợp lệ.");
        if (r.RubricScores.Count > 0 && r.RubricScores.Sum(x => x.MaxScore) > r.MaxScore) throw new ApiException(ErrorCodes.ValidationFailed, "Tổng điểm tối đa rubric vượt điểm tối đa.");
    }
    private static GradeDto Empty(Guid id) => new(id, GradingStatus.NotGraded, null, 10, [], null, [], null, "new");
    private static GradeDto ToDto(Grade g) => new(g.SubmissionId, g.Status, g.Score, g.MaxScore, g.RubricScores.OrderBy(x => x.Order).Select(x => new RubricScoreDto(x.CriterionKey, x.Title, x.Score, x.MaxScore, x.Comment, x.Order)).ToList(), g.GeneralComment, g.Attachments.Select(x => new FileDescriptorDto(x.Id, x.OriginalName, x.SizeBytes, x.Sha256, x.MimeType, $"/api/v1/grading/submissions/{g.SubmissionId}/attachments/{x.Id}/content")).ToList(), g.ReturnedAtUtc, g.RowVersion);
    private static object ToCloud(Grade x) => new
    {
        id = x.Id,
        submission_id = x.SubmissionId,
        status = x.Status.ToString(),
        score = x.Score,
        max_score = x.MaxScore,
        general_comment = x.GeneralComment,
        grader_id = x.GraderId,
        graded_at = x.GradedAtUtc,
        returned_at = x.ReturnedAtUtc,
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc
    };
    private static object ToCloud(RubricScore x) => new
    {
        id = x.Id,
        grade_id = x.GradeId,
        criterion_key = x.CriterionKey,
        title = x.Title,
        score = x.Score,
        max_score = x.MaxScore,
        comment = x.Comment,
        sort_order = x.Order,
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc
    };

    private static object ToCloud(GradedAttachment x) => new
    {
        id = x.Id,
        grade_id = x.GradeId,
        name = x.OriginalName,
        size_bytes = x.SizeBytes,
        sha256 = x.Sha256,
        mime_type = x.MimeType,
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc
    };
    private static string E(string s) => s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
