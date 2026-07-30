using System.Text.Json;
using System.Globalization;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.Infrastructure.Services;

public sealed class QuizGradingService(
    AppDbContext db,
    IAuditService audit,
    IOutboxService outbox,
    IRealtimePublisher realtime,
    ICloudAdapter? cloud = null) : IQuizGradingService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PagedResult<GradingWorkItemDto>> GetWorkItemsAsync(
        GradingStatus? status,
        int page,
        int pageSize,
        Guid actorId,
        string? organizationId,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var files = await db.SubmissionsSet.AsNoTracking()
            .Include(x => x.Participant)
            .Include(x => x.Files)
            .Include(x => x.Session).ThenInclude(x => x.Exam)
            .Where(x => x.IsOfficial
                && (x.Status == SubmissionStatus.Submitted || x.Status == SubmissionStatus.LateSubmitted))
            .ToListAsync(cancellationToken);
        var fileGradeBySubmission = await db.GradesSet.AsNoTracking()
            .Where(x => files.Select(s => s.Id).Contains(x.SubmissionId))
            .ToDictionaryAsync(x => x.SubmissionId, cancellationToken);
        var quizzes = await db.QuizAttemptsSet.AsNoTracking()
            .Include(x => x.Participant)
            .Include(x => x.Session).ThenInclude(x => x.Exam)
            .Where(x => x.Status == QuizAttemptStatus.Finalized)
            .ToListAsync(cancellationToken);

        var items = new List<GradingWorkItemDto>();
        foreach (var submission in files)
        {
            if (!await CanAccessExamAsync(submission.Session.Exam, organizationId, cancellationToken))
                continue;
            fileGradeBySubmission.TryGetValue(submission.Id, out var grade);
            items.Add(new(
                submission.Id,
                GradingWorkItemType.FileSubmission,
                submission.SessionId,
                submission.ParticipantId,
                submission.Participant.StudentCode,
                submission.Participant.DisplayName,
                submission.Session.Exam.Title,
                submission.ServerReceivedAtUtc ?? submission.ClientSubmittedAtUtc,
                grade?.Status ?? GradingStatus.NotGraded,
                null,
                grade?.Score,
                10.00m,
                submission.Files.OrderBy(x => x.OriginalName).Select(x => (Guid?)x.Id).FirstOrDefault()));
        }
        foreach (var attempt in quizzes)
        {
            if (!await CanAccessExamAsync(attempt.Session.Exam, organizationId, cancellationToken))
                continue;
            items.Add(new(
                attempt.Id,
                GradingWorkItemType.QuizAttempt,
                attempt.SessionId,
                attempt.ParticipantId,
                attempt.Participant.StudentCode,
                attempt.Participant.DisplayName,
                attempt.Session.Exam.Title,
                attempt.FinalizedAtUtc ?? attempt.UpdatedAtUtc,
                attempt.GradingStatus,
                attempt.AutoScore,
                attempt.Score,
                10.00m));
        }
        if (status.HasValue)
            items = items.Where(x => x.Status == status.Value).ToList();
        var ordered = items.OrderByDescending(x => x.SubmittedAtUtc).ThenBy(x => x.StudentCode).ToList();
        return new(
            ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            page,
            pageSize,
            ordered.Count);
    }

    public async Task<QuizGradeDetailDto> GetAsync(
        Guid attemptId,
        Guid actorId,
        string? organizationId,
        CancellationToken cancellationToken)
    {
        var attempt = await TeacherAttemptAsync(attemptId, organizationId, cancellationToken);
        return await ToTeacherDtoAsync(attempt, revealCorrect: true, cancellationToken);
    }

    public async Task<QuizGradeDetailDto> SaveAsync(
        Guid attemptId,
        SaveQuizGradeRequest request,
        Guid actorId,
        string? organizationId,
        CancellationToken cancellationToken)
    {
        var attempt = await TeacherAttemptAsync(attemptId, organizationId, cancellationToken);
        EnsureFinalized(attempt);
        if (request.Score is < 0 or > 10)
            throw new ApiException(ErrorCodes.ValidationFailed, "Điểm phải nằm trong khoảng 0 đến 10.");
        if (IsPublicCloud(attempt))
        {
            var cloudResult = await RequireCloud().SavePublicQuizGradeAsync(
                attempt.Id,
                request.Score,
                request.GeneralComment,
                RequireCloudVersion(request.RowVersion),
                RequireMutationRequestId(request.MutationRequestId),
                cancellationToken);
            ApplyCloudMutation(attempt, cloudResult);
            return await ToTeacherDtoAsync(attempt, revealCorrect: true, cancellationToken);
        }
        EnsureConcurrency(attempt, request.RowVersion);
        if (attempt.GradingStatus == GradingStatus.Returned)
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Kết quả đã công bố; cần mở lại trước khi sửa.", 409);
        var before = await ToTeacherDtoAsync(attempt, revealCorrect: true, cancellationToken);
        attempt.Score = request.Score ?? attempt.AutoScore;
        attempt.MaxScore = 10.00m;
        attempt.GeneralComment = request.GeneralComment?.Trim();
        attempt.GradingStatus = GradingStatus.Graded;
        attempt.GraderId = actorId;
        attempt.GradedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        var result = await ToTeacherDtoAsync(attempt, revealCorrect: true, cancellationToken);
        await audit.WriteAsync(
            "QuizGradeSaved",
            nameof(QuizAttempt),
            attempt.Id.ToString(),
            attempt.SessionId,
            before,
            result,
            cancellationToken);
        await EnqueueAsync(attempt, cancellationToken);
        return result;
    }

    public async Task<QuizGradeDetailDto> ReturnAsync(
        Guid attemptId,
        ReturnQuizGradeRequest request,
        Guid actorId,
        string? organizationId,
        CancellationToken cancellationToken)
    {
        var attempt = await TeacherAttemptAsync(attemptId, organizationId, cancellationToken);
        EnsureFinalized(attempt);
        if (IsPublicCloud(attempt))
        {
            var result = await RequireCloud().ReturnPublicQuizGradeAsync(
                attempt.Id,
                request.Message,
                RequireCloudVersion(request.RowVersion),
                RequireMutationRequestId(request.MutationRequestId),
                cancellationToken);
            ApplyCloudMutation(attempt, result);
            return await ToTeacherDtoAsync(attempt, revealCorrect: true, cancellationToken);
        }
        if (attempt.GradingStatus == GradingStatus.Returned)
            return await ToTeacherDtoAsync(attempt, revealCorrect: true, cancellationToken);
        EnsureConcurrency(attempt, request.RowVersion);
        if (attempt.Score is null or < 0 or > 10)
            throw new ApiException(ErrorCodes.ValidationFailed, "Chưa có điểm hợp lệ để công bố.");
        var returnedAt = DateTimeOffset.UtcNow;
        attempt.GradingStatus = GradingStatus.Returned;
        attempt.ReturnedAtUtc = returnedAt;
        attempt.GraderId = actorId;
        attempt.GradedAtUtc ??= returnedAt;
        var session = attempt.Session;
        session.Sequence++;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            "QuizGradeReturned",
            nameof(QuizAttempt),
            attempt.Id.ToString(),
            attempt.SessionId,
            null,
            new { attempt.Score, attempt.MaxScore, request.Message, returnedAt },
            cancellationToken);
        await EnqueueAsync(attempt, cancellationToken);
        await realtime.PublishParticipantAsync(
            attempt.SessionId,
            attempt.ParticipantId,
            RealtimeEvents.QuizGradeReturned,
            session.Sequence,
            new QuizGradeReturnedEvent(attempt.Id, attempt.SessionId, attempt.Score.Value, 10.00m, returnedAt),
            cancellationToken);
        return await ToTeacherDtoAsync(attempt, revealCorrect: true, cancellationToken);
    }

    public async Task<QuizGradeDetailDto> ReopenAsync(
        Guid attemptId,
        ReopenQuizGradeRequest request,
        Guid actorId,
        string? organizationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new ApiException(ErrorCodes.ValidationFailed, "Phải có lý do mở lại kết quả.");
        var attempt = await TeacherAttemptAsync(attemptId, organizationId, cancellationToken);
        EnsureFinalized(attempt);
        if (IsPublicCloud(attempt))
        {
            var cloudResult = await RequireCloud().ReopenPublicQuizGradeAsync(
                attempt.Id,
                request.Reason,
                RequireCloudVersion(request.RowVersion),
                RequireMutationRequestId(request.MutationRequestId),
                cancellationToken);
            ApplyCloudMutation(attempt, cloudResult);
            return await ToTeacherDtoAsync(attempt, revealCorrect: true, cancellationToken);
        }
        EnsureConcurrency(attempt, request.RowVersion);
        if (attempt.GradingStatus != GradingStatus.Returned)
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Chỉ mở lại kết quả đã công bố.", 409);
        var before = await ToTeacherDtoAsync(attempt, revealCorrect: true, cancellationToken);
        attempt.GradingStatus = GradingStatus.InProgress;
        attempt.ReturnedAtUtc = null;
        attempt.GraderId = actorId;
        await db.SaveChangesAsync(cancellationToken);
        var result = await ToTeacherDtoAsync(attempt, revealCorrect: true, cancellationToken);
        await audit.WriteAsync(
            "QuizGradeReopened",
            nameof(QuizAttempt),
            attempt.Id.ToString(),
            attempt.SessionId,
            before,
            new { grade = result, request.Reason },
            cancellationToken);
        await EnqueueAsync(attempt, cancellationToken);
        return result;
    }

    public async Task<StudentQuizReviewDto> GetStudentReviewAsync(
        Guid attemptId,
        Guid participantId,
        CancellationToken cancellationToken)
    {
        var attempt = await db.QuizAttemptsSet.AsNoTracking()
            .Include(x => x.Answers)
            .Include(x => x.Session)
            .FirstOrDefaultAsync(
                x => x.Id == attemptId && x.ParticipantId == participantId,
                cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy bài làm trắc nghiệm.", 404);
        EnsureFinalized(attempt);
        var returned = attempt.ReturnedAtUtc.HasValue;
        var scoreVisible = returned
            || attempt.ResultPolicySnapshot == QuizResultPolicy.ShowAfterSubmission;
        return new(
            attempt.Id,
            scoreVisible ? attempt.Score : null,
            10.00m,
            scoreVisible,
            returned,
            returned ? attempt.GeneralComment : null,
            await BuildQuestionsAsync(attempt, revealCorrect: returned, cancellationToken));
    }

    private async Task<QuizAttempt> TeacherAttemptAsync(
        Guid attemptId,
        string? organizationId,
        CancellationToken cancellationToken)
    {
        var attempt = await db.QuizAttemptsSet
            .Include(x => x.Answers)
            .Include(x => x.Participant)
            .Include(x => x.Session).ThenInclude(x => x.Exam)
            .FirstOrDefaultAsync(x => x.Id == attemptId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy bài làm trắc nghiệm.", 404);
        if (!await CanAccessExamAsync(attempt.Session.Exam, organizationId, cancellationToken))
            throw new ApiException(ErrorCodes.Forbidden, "Không được chấm bài thuộc tổ chức khác.", 403);
        return attempt;
    }

    private async Task<bool> CanAccessExamAsync(
        Exam exam,
        string? organizationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(organizationId) || !exam.CreatedBy.HasValue)
            return true;
        var ownerOrganization = await db.UsersSet.AsNoTracking()
            .Where(x => x.Id == exam.CreatedBy.Value)
            .Select(x => x.OrganizationId)
            .SingleOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(ownerOrganization)
            || string.Equals(ownerOrganization, organizationId, StringComparison.Ordinal);
    }

    private async Task<QuizGradeDetailDto> ToTeacherDtoAsync(
        QuizAttempt attempt,
        bool revealCorrect,
        CancellationToken cancellationToken) =>
        new(
            attempt.Id,
            attempt.SessionId,
            attempt.ParticipantId,
            attempt.Participant.StudentCode,
            attempt.Participant.DisplayName,
            attempt.Session.Exam.Title,
            attempt.AutoScore,
            attempt.Score,
            10.00m,
            attempt.GradingStatus,
            attempt.GeneralComment,
            attempt.GraderId,
            attempt.GradedAtUtc,
            attempt.ReturnedAtUtc,
            IsPublicCloud(attempt)
                ? attempt.CloudVersion.ToString(CultureInfo.InvariantCulture)
                : attempt.RowVersion,
            await BuildQuestionsAsync(attempt, revealCorrect, cancellationToken));

    private async Task<IReadOnlyList<QuizQuestionReviewDto>> BuildQuestionsAsync(
        QuizAttempt attempt,
        bool revealCorrect,
        CancellationToken cancellationToken)
    {
        var snapshot = JsonSerializer.Deserialize<List<QuizQuestionDto>>(attempt.SnapshotJson, Json) ?? [];
        var questionIds = snapshot.Select(x => x.Id).ToList();
        var correctByQuestion = await db.QuizChoicesSet.AsNoTracking()
            .Where(x => questionIds.Contains(x.QuestionId) && x.IsCorrect)
            .GroupBy(x => x.QuestionId)
            .ToDictionaryAsync(
                x => x.Key,
                x => x.Select(c => c.Id).ToHashSet(),
                cancellationToken);
        var selectedByQuestion = attempt.Answers.ToDictionary(
            x => x.QuestionId,
            x => (JsonSerializer.Deserialize<List<Guid>>(x.ChoiceIdsJson, Json) ?? []).ToHashSet());
        return snapshot.OrderBy(x => x.Order).Select(question =>
        {
            selectedByQuestion.TryGetValue(question.Id, out var selected);
            selected ??= [];
            correctByQuestion.TryGetValue(question.Id, out var correct);
            correct ??= [];
            var earned = revealCorrect
                ? (correct.SetEquals(selected) ? question.Points : 0m)
                : (decimal?)null;
            return new QuizQuestionReviewDto(
                question.Id,
                question.Text,
                question.Order,
                question.Points,
                earned,
                question.Choices.OrderBy(x => x.Order).Select(choice => new QuizChoiceReviewDto(
                    choice.Id,
                    choice.Text,
                    choice.Order,
                    selected.Contains(choice.Id),
                    revealCorrect ? correct.Contains(choice.Id) : null)).ToList());
        }).ToList();
    }

    private static void EnsureFinalized(QuizAttempt attempt)
    {
        if (attempt.Status != QuizAttemptStatus.Finalized)
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Chỉ chấm bài đã finalize.", 409);
    }

    private static void EnsureConcurrency(QuizAttempt attempt, string rowVersion)
    {
        if (string.IsNullOrWhiteSpace(rowVersion)
            || !string.Equals(attempt.RowVersion, rowVersion, StringComparison.Ordinal))
            throw new ApiException(ErrorCodes.ConcurrencyConflict, "Bài chấm đã được cập nhật ở nơi khác.", 409);
    }

    private static bool IsPublicCloud(QuizAttempt attempt) =>
        string.Equals(
            attempt.SourceMode,
            "PublicCloud",
            StringComparison.OrdinalIgnoreCase);

    private static long RequireCloudVersion(string rowVersion)
    {
        if (!long.TryParse(
                rowVersion,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var cloudVersion)
            || cloudVersion < 1)
        {
            throw new ApiException(
                ErrorCodes.ConcurrencyConflict,
                "PublicCloud grading version không hợp lệ; hãy tải lại bài chấm.",
                409);
        }
        return cloudVersion;
    }

    private static Guid RequireMutationRequestId(Guid requestId)
    {
        if (requestId == Guid.Empty)
            throw new ApiException(
                ErrorCodes.ValidationFailed,
                "Thiếu MutationRequestId cho thao tác chấm PublicCloud.");
        return requestId;
    }

    private static void ApplyCloudMutation(
        QuizAttempt attempt,
        CloudQuizGradeMutationResult result)
    {
        if (result.AttemptId != attempt.Id
            || result.SessionId != attempt.SessionId
            || result.ParticipantId != attempt.ParticipantId
            || result.CloudVersion < 1
            || result.MaxScore != 10.00m)
        {
            throw new ApiException(
                ErrorCodes.CloudUploadFailed,
                "Supabase trả contract chấm PublicCloud không khớp bài đang mở.",
                502);
        }

        attempt.AutoScore = result.AutoScore;
        attempt.Score = result.Score;
        attempt.MaxScore = result.MaxScore;
        attempt.GradingStatus = result.Status;
        attempt.GeneralComment = result.GeneralComment;
        attempt.GraderId = result.GraderId;
        attempt.GradedAtUtc = result.GradedAtUtc;
        attempt.ReturnedAtUtc = result.ReturnedAtUtc;
        attempt.CloudVersion = result.CloudVersion;
        attempt.CloudUpdatedAtUtc = result.UpdatedAtUtc;
        attempt.CloudSyncState = "Pulled";
    }

    private ICloudAdapter RequireCloud() =>
        cloud ?? throw new ApiException(
            ErrorCodes.CloudOffline,
            "PublicCloud chưa được cấu hình cho thao tác chấm của giáo viên.",
            503);

    private Task EnqueueAsync(QuizAttempt attempt, CancellationToken cancellationToken) =>
        outbox.EnqueueAsync(
            "quiz_attempts",
            attempt.Id.ToString(),
            "upsert",
            new
            {
                id = attempt.Id,
                session_id = attempt.SessionId,
                participant_id = attempt.ParticipantId,
                exam_version = attempt.ExamVersion,
                result_policy = attempt.ResultPolicySnapshot.ToString(),
                status = attempt.Status.ToString(),
                started_at = attempt.StartedAtUtc,
                deadline_at = attempt.DeadlineUtc,
                finalized_at = attempt.FinalizedAtUtc,
                auto_score = attempt.AutoScore,
                score = attempt.Score,
                max_score = 10.00m,
                grading_status = attempt.GradingStatus.ToString(),
                general_comment = attempt.GeneralComment,
                grader_id = attempt.GraderId,
                graded_at = attempt.GradedAtUtc,
                returned_at = attempt.ReturnedAtUtc,
                snapshot_json = attempt.SnapshotJson,
                finalize_idempotency_key = attempt.FinalizeIdempotencyKey,
                created_at = attempt.CreatedAtUtc,
                updated_at = attempt.UpdatedAtUtc
            },
            cancellationToken: cancellationToken);
}
