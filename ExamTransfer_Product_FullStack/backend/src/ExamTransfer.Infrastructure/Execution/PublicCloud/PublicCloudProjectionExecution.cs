using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ExamTransfer.Infrastructure.Execution.PublicCloud;

public sealed class PublicCloudProjectionExecution(
    AppDbContext db,
    ICloudSyncSignal? cloudSyncSignal = null)
{
    private const string ExamsEntityType = "exams";
    private const string QuestionsEntityType = "quiz_questions";
    private const string ChoicesEntityType = "quiz_choices";
    private const string SessionsEntityType = "exam_sessions";
    private const int SqliteIdBatchSize = 400;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<CloudProjectionReadiness> GetProjectionReadinessAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var session = await LoadSessionScopeAsync(id, cancellationToken);
        if (session.AccessMode != SessionAccessMode.PublicCloud)
            return new(
                id,
                false,
                true,
                SyncStatus.LocalOnly,
                "LAN_ONLY",
                "Phiên LAN không cần PublicCloud projection.",
                0);

        if (session.DeliveryTypeSnapshot != ExamDeliveryType.MultipleChoice)
            return await GetFileSubmissionReadinessAsync(id, cancellationToken);

        var graph = await LoadQuizGraphAsync(session, cancellationToken);
        if (graph.QuestionIds.Count == 0)
            return new(
                id,
                true,
                false,
                SyncStatus.Pending,
                ErrorCodes.QuizHasNoQuestions,
                "Đề trắc nghiệm chưa có câu hỏi để đồng bộ lên PublicCloud.",
                0);

        var projection = await LoadQuizProjectionAsync(
            session,
            graph,
            tracked: false,
            cancellationToken);
        if (projection.SessionItem is not null
            && IsRoomCodeConflict(projection.SessionItem))
            return RoomCodeConflictReadiness(id, projection.SessionItem);

        var retryCount = projection.Items.Count == 0
            ? 0
            : projection.Items.Max(x => x.RetryCount);
        if (projection.HasMissingRows)
            return new(
                id,
                true,
                false,
                SyncStatus.Pending,
                ErrorCodes.PublicCloudQuizProjectionNotReady,
                "Đang đồng bộ nội dung trắc nghiệm lên PublicCloud.",
                retryCount);

        var failed = projection.Items.FirstOrDefault(
            x => x.Status is SyncStatus.Failed or SyncStatus.Conflict);
        if (failed is not null)
            return new(
                id,
                true,
                false,
                failed.Status,
                "PUBLICCLOUD_QUIZ_PROJECTION_FAILED",
                "Đồng bộ nội dung trắc nghiệm thất bại. Hãy thử đồng bộ lại.",
                retryCount);

        var pending = projection.Items.FirstOrDefault(x => x.Status != SyncStatus.Synced);
        if (pending is not null)
            return new(
                id,
                true,
                false,
                pending.Status,
                "PUBLICCLOUD_QUIZ_PROJECTION_PENDING",
                "Đang đồng bộ nội dung trắc nghiệm lên PublicCloud.",
                retryCount);

        return new(
            id,
            true,
            true,
            SyncStatus.Synced,
            "PUBLICCLOUD_QUIZ_PROJECTION_READY",
            "Nội dung trắc nghiệm đã sẵn sàng trên PublicCloud.",
            retryCount);
    }

    public async Task<CloudProjectionReadiness> RetryProjectionAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var session = await LoadSessionScopeAsync(id, cancellationToken);
        if (session.AccessMode != SessionAccessMode.PublicCloud)
            return await GetProjectionReadinessAsync(id, cancellationToken);

        if (session.DeliveryTypeSnapshot != ExamDeliveryType.MultipleChoice)
            return await RetryFileSubmissionProjectionAsync(id, cancellationToken);

        var graph = await LoadQuizGraphAsync(session, cancellationToken);
        var projection = await LoadQuizProjectionAsync(
            session,
            graph,
            tracked: true,
            cancellationToken);
        if (projection.SessionItem is not null
            && IsRoomCodeConflict(projection.SessionItem))
            return await GetProjectionReadinessAsync(id, cancellationToken);

        var retryItems = projection.Items
            .Where(x => x.Status is not (SyncStatus.Synced or SyncStatus.Syncing))
            .ToList();
        if (retryItems.Count == 0 && projection.MissingRows.Count == 0)
            return await GetProjectionReadinessAsync(id, cancellationToken);

        var missingPayloads = await LoadMissingPayloadsAsync(
            session,
            graph,
            projection.MissingRows,
            cancellationToken);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var item in retryItems)
                PrepareRetry(item);

            foreach (var missing in projection.MissingRows)
            {
                db.SyncQueueSet.Add(new SyncQueueItem
                {
                    EntityType = missing.EntityType,
                    EntityId = missing.EntityId.ToString(),
                    Operation = "upsert",
                    PayloadJson = JsonSerializer.Serialize(
                        missingPayloads[(missing.EntityType, missing.EntityId)],
                        JsonOptions),
                    Status = SyncStatus.Pending,
                    NextRetryAtUtc = DateTimeOffset.UtcNow
                });
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        cloudSyncSignal?.Pulse();

        return await GetProjectionReadinessAsync(id, cancellationToken);
    }

    internal static bool IsRoomCodeConflict(SyncQueueItem item) =>
        item.Status == SyncStatus.Conflict
        && string.Equals(
            ParseFailure(item.LastError)?.Code,
            ErrorCodes.RoomCodeConflict,
            StringComparison.Ordinal);

    private async Task<ExamSession> LoadSessionScopeAsync(
        Guid id,
        CancellationToken cancellationToken) =>
        await db.ExamSessionsSet
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
        ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy phòng thi.", 404);

    private async Task<CloudProjectionReadiness> GetFileSubmissionReadinessAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var projectionItems = await LoadLatestItemsAsync(
            SessionsEntityType,
            [id],
            tracked: false,
            cancellationToken);
        if (!projectionItems.TryGetValue(id.ToString(), out var item))
            return new(
                id,
                true,
                false,
                SyncStatus.Pending,
                "PUBLICCLOUD_PROJECTION_PENDING",
                "Phòng đang chờ đồng bộ PublicCloud.",
                0);

        if (IsRoomCodeConflict(item))
            return RoomCodeConflictReadiness(id, item);

        var failure = ParseFailure(item.LastError);
        return item.Status switch
        {
            SyncStatus.Synced => new(
                id,
                true,
                true,
                item.Status,
                "PUBLICCLOUD_PROJECTION_READY",
                "Sẵn sàng — có thể chia sẻ mã phòng.",
                item.RetryCount),
            SyncStatus.Failed or SyncStatus.Conflict => new(
                id,
                true,
                false,
                item.Status,
                failure?.Code ?? "PUBLICCLOUD_PROJECTION_FAILED",
                failure is null
                    ? "Đồng bộ PublicCloud thất bại — dữ liệu cục bộ vẫn được giữ. Hãy thử lại."
                    : $"Đồng bộ PublicCloud thất bại ({failure.Code}) — dữ liệu cục bộ vẫn được giữ.",
                item.RetryCount),
            _ => new(
                id,
                true,
                false,
                item.Status,
                "PUBLICCLOUD_PROJECTION_SYNCING",
                "Đang đồng bộ PublicCloud.",
                item.RetryCount)
        };
    }

    private async Task<CloudProjectionReadiness> RetryFileSubmissionProjectionAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var projectionItems = await LoadLatestItemsAsync(
            SessionsEntityType,
            [id],
            tracked: true,
            cancellationToken);
        if (!projectionItems.TryGetValue(id.ToString(), out var item))
            throw new ApiException(
                ErrorCodes.Conflict,
                "Không tìm thấy outbox PublicCloud của phòng thi; dữ liệu cục bộ không bị thay đổi.",
                409);
        if (IsRoomCodeConflict(item))
            return await GetProjectionReadinessAsync(id, cancellationToken);
        if (item.Status != SyncStatus.Synced)
        {
            PrepareRetry(item);
            await db.SaveChangesAsync(cancellationToken);
            cloudSyncSignal?.Pulse();
        }
        return await GetProjectionReadinessAsync(id, cancellationToken);
    }

    private async Task<QuizGraph> LoadQuizGraphAsync(
        ExamSession session,
        CancellationToken cancellationToken)
    {
        var questions = await db.QuizQuestionsSet
            .AsNoTracking()
            .Where(x => x.ExamId == session.ExamId
                && x.Version == session.ExamVersionSnapshot)
            .ToListAsync(cancellationToken);
        var questionIds = questions.Select(x => x.Id).ToList();
        if (questionIds.Count == 0)
            return new([], [], [], []);

        var choices = await db.QuizChoicesSet
            .AsNoTracking()
            .Where(x => questionIds.Contains(x.QuestionId))
            .ToListAsync(cancellationToken);
        return new(
            questions,
            choices,
            questionIds,
            choices.Select(x => x.Id).ToList());
    }

    private async Task<QuizProjection> LoadQuizProjectionAsync(
        ExamSession session,
        QuizGraph graph,
        bool tracked,
        CancellationToken cancellationToken)
    {
        var examItems = await LoadLatestItemsAsync(
            ExamsEntityType,
            [session.ExamId],
            tracked,
            cancellationToken);
        var questionItems = await LoadLatestItemsAsync(
            QuestionsEntityType,
            graph.QuestionIds,
            tracked,
            cancellationToken);
        var choiceItems = await LoadLatestItemsAsync(
            ChoicesEntityType,
            graph.ChoiceIds,
            tracked,
            cancellationToken);
        var sessionItems = await LoadLatestItemsAsync(
            SessionsEntityType,
            [session.Id],
            tracked,
            cancellationToken);

        var items = new List<SyncQueueItem>(
            2 + graph.QuestionIds.Count + graph.ChoiceIds.Count);
        var missingRows = new List<MissingProjectionRow>();
        AddExpected(ExamsEntityType, examItems, [session.ExamId]);
        AddExpected(QuestionsEntityType, questionItems, graph.QuestionIds);
        AddExpected(ChoicesEntityType, choiceItems, graph.ChoiceIds);
        AddExpected(SessionsEntityType, sessionItems, [session.Id]);
        sessionItems.TryGetValue(session.Id.ToString(), out var sessionItem);
        return new(items, missingRows, sessionItem);

        void AddExpected(
            string entityType,
            IReadOnlyDictionary<string, SyncQueueItem> latest,
            IReadOnlyCollection<Guid> expectedIds)
        {
            foreach (var expectedId in expectedIds)
            {
                if (latest.TryGetValue(expectedId.ToString(), out var item))
                    items.Add(item);
                else
                    missingRows.Add(new(entityType, expectedId));
            }
        }
    }

    private async Task<IReadOnlyDictionary<(string EntityType, Guid EntityId), object>>
        LoadMissingPayloadsAsync(
            ExamSession session,
            QuizGraph graph,
            IReadOnlyCollection<MissingProjectionRow> missingRows,
            CancellationToken cancellationToken)
    {
        var payloads = new Dictionary<(string EntityType, Guid EntityId), object>();
        var questions = graph.Questions.ToDictionary(x => x.Id);
        var choices = graph.Choices.ToDictionary(x => x.Id);
        Exam? exam = null;
        if (missingRows.Any(x => x.EntityType == ExamsEntityType))
        {
            exam = await db.ExamsSet
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == session.ExamId, cancellationToken)
                ?? throw MissingLocalEntity(ExamsEntityType, session.ExamId);
        }

        foreach (var missing in missingRows)
        {
            payloads[(missing.EntityType, missing.EntityId)] = missing.EntityType switch
            {
                ExamsEntityType when exam is not null =>
                    PublicCloudProjectionPayloads.Exam(exam),
                QuestionsEntityType when questions.TryGetValue(missing.EntityId, out var question) =>
                    PublicCloudProjectionPayloads.Question(question),
                ChoicesEntityType when choices.TryGetValue(missing.EntityId, out var choice) =>
                    PublicCloudProjectionPayloads.Choice(choice),
                SessionsEntityType when missing.EntityId == session.Id =>
                    PublicCloudProjectionPayloads.Session(session),
                _ => throw MissingLocalEntity(missing.EntityType, missing.EntityId)
            };
        }
        return payloads;
    }

    private static ApiException MissingLocalEntity(string entityType, Guid entityId) =>
        new(
            ErrorCodes.PublicCloudQuizProjectionNotReady,
            $"Không tìm thấy dữ liệu local bắt buộc cho projection {entityType}/{entityId}.",
            409);

    private async Task<IReadOnlyDictionary<string, SyncQueueItem>> LoadLatestItemsAsync(
        string entityType,
        IReadOnlyCollection<Guid> entityIds,
        bool tracked,
        CancellationToken cancellationToken)
    {
        if (entityIds.Count == 0)
            return new Dictionary<string, SyncQueueItem>(StringComparer.OrdinalIgnoreCase);

        var rows = new List<SyncQueueItem>();
        foreach (var idBatch in entityIds
                     .Select(x => x.ToString())
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Chunk(SqliteIdBatchSize))
        {
            IQueryable<SyncQueueItem> query = db.SyncQueueSet;
            if (!tracked)
                query = query.AsNoTracking();
            rows.AddRange(await query
                .Where(x => x.EntityType == entityType
                    && idBatch.Contains(x.EntityId))
                .ToListAsync(cancellationToken));
        }

        return rows
            .GroupBy(x => x.EntityId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(x => x.CreatedAtUtc)
                    .ThenByDescending(x => x.Id)
                    .First(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static CloudProjectionReadiness RoomCodeConflictReadiness(
        Guid id,
        SyncQueueItem item) =>
        new(
            id,
            true,
            false,
            SyncStatus.Conflict,
            ErrorCodes.RoomCodeConflict,
            "Mã phòng PublicCloud đang được sử dụng trong tổ chức. Hãy nhập mã khác hoặc để trống để sinh mã mới.",
            item.RetryCount);

    private static void PrepareRetry(SyncQueueItem item)
    {
        item.Status = SyncStatus.Pending;
        item.LastError = null;
        item.LeaseUntilUtc = null;
        item.NextRetryAtUtc = DateTimeOffset.UtcNow;
        item.CompletedAtUtc = null;
    }

    private static CloudSyncFailure? ParseFailure(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            return JsonSerializer.Deserialize<CloudSyncFailure>(value, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record QuizGraph(
        IReadOnlyList<QuizQuestion> Questions,
        IReadOnlyList<QuizChoice> Choices,
        IReadOnlyList<Guid> QuestionIds,
        IReadOnlyList<Guid> ChoiceIds);

    private sealed record QuizProjection(
        IReadOnlyList<SyncQueueItem> Items,
        IReadOnlyList<MissingProjectionRow> MissingRows,
        SyncQueueItem? SessionItem)
    {
        public bool HasMissingRows => MissingRows.Count > 0;
    }

    private sealed record MissingProjectionRow(string EntityType, Guid EntityId);
}
