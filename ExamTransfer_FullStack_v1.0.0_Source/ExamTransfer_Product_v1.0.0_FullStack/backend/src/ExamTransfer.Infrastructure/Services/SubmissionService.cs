using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Infrastructure.Storage;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ExamTransfer.Infrastructure.Services;

public sealed class SubmissionService(AppDbContext db, IStoragePaths paths, IChunkStorage chunks, IReceiptSigner receipts, IAuditService audit, IOutboxService outbox, IRealtimePublisher realtime, IOptions<ExamTransferOptions> options) : ISubmissionService
{
    private readonly ExamTransferOptions _options = options.Value;

    public async Task<InitSubmissionResponse> InitAsync(InitSubmissionRequest request, CancellationToken cancellationToken)
    {
        if (request.Files.Count != StudentSubmissionPolicy.MaxFileCount)
            throw new ApiException(ErrorCodes.SubmissionFileCountInvalid, "Bài nộp phải có đúng một file nén.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) throw new ApiException(ErrorCodes.ValidationFailed, "Thiếu idempotencyKey.");
        var existing = await db.SubmissionsSet.Include(x => x.Files).FirstOrDefaultAsync(x => x.ParticipantId == request.ParticipantId && x.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (existing is not null) return ToInitResponse(existing);

        var participant = await db.SessionParticipantsSet.Include(x => x.Session).ThenInclude(x => x.Exam).FirstOrDefaultAsync(x => x.Id == request.ParticipantId && x.SessionId == request.SessionId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy người tham gia.", 404);
        if (participant.Status != ParticipantStatus.Approved) throw new ApiException(ErrorCodes.Forbidden, "Người tham gia chưa được duyệt.", 403);
        if (participant.Session.Status is not (SessionStatus.InProgress or SessionStatus.Collecting)) throw new ApiException(ErrorCodes.InvalidStateTransition, "Phòng chưa cho phép nộp bài.", 409);
        var hasFinalizedAttempt = await db.SubmissionsSet.AnyAsync(x => x.ParticipantId == participant.Id && (x.Status == SubmissionStatus.Submitted || x.Status == SubmissionStatus.LateSubmitted), cancellationToken);
        if (hasFinalizedAttempt && !participant.ResubmitAllowed)
            throw new ApiException(ErrorCodes.Conflict, "Đã có bài nộp; giáo viên chưa cho phép nộp lại.", 409);

        if (participant.Session.Exam.DeliveryType != ExamDeliveryType.FileSubmission)
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Bài thi trắc nghiệm không sử dụng luồng nộp file.", 409);
        ValidateFiles(request.Files);
        var attempt = (await db.SubmissionsSet.Where(x => x.ParticipantId == request.ParticipantId).MaxAsync(x => (int?)x.AttemptNumber, cancellationToken) ?? 0) + 1;
        var deadline = participant.Session.StartedAtUtc!.Value.AddMinutes(participant.Session.Exam.DurationMinutes + participant.ExtraTimeMinutes);
        var submission = new Submission
        {
            SessionId = request.SessionId, ParticipantId = request.ParticipantId, AttemptNumber = attempt, IdempotencyKey = request.IdempotencyKey,
            Status = SubmissionStatus.Uploading, ClientSubmittedAtUtc = request.ClientSubmittedAtUtc, DeadlineUtc = deadline, IsOfficial = false
        };
        foreach (var input in request.Files)
        {
            var fileId = Guid.NewGuid(); var storedName = fileId.ToString("N") + Path.GetExtension(input.Name).ToLowerInvariant();
            var transferRoot = Path.Combine(paths.SessionRoot(request.SessionId), "temporary", submission.Id.ToString("N"), fileId.ToString("N"), "chunks");
            submission.Files.Add(new SubmissionFile
            {
                Id = fileId, ClientFileId = input.ClientFileId, OriginalName = Path.GetFileName(input.Name), StoredName = storedName, MimeType = input.MimeType,
                SizeBytes = input.SizeBytes, Sha256 = input.Sha256.ToLowerInvariant(), ChunkSizeBytes = _options.Transfer.ChunkSizeBytes,
                TotalChunks = (int)Math.Ceiling(input.SizeBytes / (double)_options.Transfer.ChunkSizeBytes), TemporaryPath = transferRoot, TransferStatus = TransferStatus.Running
            });
        }
        db.SubmissionsSet.Add(submission); participant.SubmissionStatus = SubmissionStatus.Uploading; participant.ResubmitAllowed = false; participant.Session.Sequence++;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("SubmissionStarted", nameof(Submission), submission.Id.ToString(), submission.SessionId, null, new { submission.ParticipantId, submission.AttemptNumber, fileCount = submission.Files.Count }, cancellationToken);
        await realtime.PublishSessionAsync(submission.SessionId, RealtimeEvents.SubmissionStarted, participant.Session.Sequence, new { submissionId = submission.Id, participantId = participant.Id, attempt }, cancellationToken);
        return ToInitResponse(submission);
    }

    public async Task UploadChunkAsync(Guid submissionId, Guid fileId, int index, Stream content, long contentLength, string? chunkSha256, CancellationToken cancellationToken)
    {
        var file = await db.SubmissionFilesSet.Include(x => x.Submission).ThenInclude(x => x.Participant).FirstOrDefaultAsync(x => x.Id == fileId && x.SubmissionId == submissionId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy file bài nộp.", 404);
        if (file.Submission.Status is SubmissionStatus.Submitted or SubmissionStatus.LateSubmitted or SubmissionStatus.Rejected) throw new ApiException(ErrorCodes.SubmissionFinalized, "Bài nộp đã đóng.", 409);
        if (index < 0 || index >= file.TotalChunks || contentLength <= 0 || contentLength > file.ChunkSizeBytes) throw new ApiException(ErrorCodes.ChunkMismatch, "Chunk không hợp lệ.");
        await chunks.WriteChunkAsync(file.TemporaryPath, index, content, file.ChunkSizeBytes, chunkSha256, cancellationToken);
        var received = chunks.ReadReceivedChunks(file.ReceivedChunksJson).ToHashSet(); received.Add(index); file.ReceivedChunksJson = chunks.WriteReceivedChunks(received); file.TransferStatus = TransferStatus.Running;
        file.Submission.Status = SubmissionStatus.Uploading; file.Submission.Participant.SubmissionStatus = SubmissionStatus.Uploading;
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<SubmissionSummaryDto> GetStatusAsync(Guid submissionId, CancellationToken cancellationToken) => GetAsync(submissionId, cancellationToken);

    public async Task<FinalizeSubmissionResponse> FinalizeAsync(Guid submissionId, FinalizeSubmissionRequest request, CancellationToken cancellationToken)
    {
        var submission = await db.SubmissionsSet.Include(x => x.Files).Include(x => x.Participant).ThenInclude(x => x.Session).ThenInclude(x => x.Exam).FirstOrDefaultAsync(x => x.Id == submissionId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy bài nộp.", 404);
        if (submission.Status is SubmissionStatus.Submitted or SubmissionStatus.LateSubmitted)
        {
            var descriptors = submission.Files.Select(ToDescriptor).ToList();
            return new FinalizeSubmissionResponse(submission.Status, submission.ServerReceivedAtUtc!.Value, submission.IsLate, submission.ReceiptCode!, submission.ReceiptSignature!, descriptors);
        }
        submission.Status = SubmissionStatus.Verifying; await db.SaveChangesAsync(cancellationToken);
        var finalRoot = paths.SubmissionRoot(submission.SessionId, submission.Participant.StudentCode, submission.Id); Directory.CreateDirectory(finalRoot);
        var completedFiles = new List<FileDescriptorDto>();
        try
        {
            foreach (var file in submission.Files)
            {
                var finalPath = Path.Combine(finalRoot, file.StoredName);
                await chunks.AssembleAndVerifyAsync(file.TemporaryPath, file.TotalChunks, file.SizeBytes, file.Sha256, finalPath, cancellationToken);
                if (!await ArchiveSignatureValidator.MatchesExtensionAsync(finalPath, file.OriginalName, cancellationToken))
                {
                    File.Delete(finalPath);
                    await audit.WriteAsync("SubmissionArchiveRejected", nameof(SubmissionFile), file.Id.ToString(), submission.SessionId, null, new { file.OriginalName, reason = ErrorCodes.SubmissionArchiveRequired }, cancellationToken);
                    throw new ApiException(ErrorCodes.SubmissionArchiveRequired, "Bài làm phải là file nén hợp lệ và chữ ký file phải khớp phần mở rộng.", 422);
                }
                file.RelativePath = Path.GetRelativePath(paths.RootPath, finalPath); file.TransferStatus = TransferStatus.Completed;
                completedFiles.Add(ToDescriptor(file));
            }
        }
        catch
        {
            submission.Status = SubmissionStatus.Failed; submission.Participant.SubmissionStatus = SubmissionStatus.Failed; await db.SaveChangesAsync(cancellationToken); throw;
        }
        var receivedAt = DateTimeOffset.UtcNow; submission.ServerReceivedAtUtc = receivedAt; submission.IsLate = receivedAt > submission.DeadlineUtc; submission.Status = submission.IsLate ? SubmissionStatus.LateSubmitted : SubmissionStatus.Submitted;
        submission.Participant.SubmissionStatus = submission.Status; submission.ClientNote = request.ClientNote;
        var previousOfficial = await db.SubmissionsSet.Where(x => x.ParticipantId == submission.ParticipantId && x.IsOfficial).ToListAsync(cancellationToken);
        foreach (var old in previousOfficial) old.IsOfficial = false;
        submission.IsOfficial = true;
        var signed = receipts.Create(submission.Id, receivedAt, completedFiles); submission.ReceiptCode = signed.ReceiptCode; submission.ReceiptSignature = signed.Signature; submission.Participant.Session.Sequence++;
        await db.SaveChangesAsync(cancellationToken);
        var receiptRoot = paths.ReceiptRoot(submission.SessionId); Directory.CreateDirectory(receiptRoot);
        var receiptPath = Path.Combine(receiptRoot, submission.Id.ToString("N") + ".json");
        await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(new { submissionId = submission.Id, signed.ReceiptCode, signed.Signature, serverReceivedAtUtc = receivedAt, submission.IsLate, files = completedFiles }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), cancellationToken);
        await audit.WriteAsync("SubmissionAccepted", nameof(Submission), submission.Id.ToString(), submission.SessionId, null, new { submission.Status, submission.IsLate, submission.ReceiptCode }, cancellationToken);
        await outbox.EnqueueAsync("submissions", submission.Id.ToString(), "upsert", ToCloud(submission), cancellationToken: cancellationToken);
        foreach (var file in submission.Files)
        {
            var fullPath = Path.Combine(paths.RootPath, file.RelativePath);
            await outbox.EnqueueAsync(
                "submission_files",
                file.Id.ToString(),
                "upsert",
                ToCloud(file),
                fullPath,
                cancellationToken);
        }

        await realtime.PublishSessionAsync(submission.SessionId, RealtimeEvents.SubmissionAccepted, submission.Participant.Session.Sequence, new SubmissionAcceptedEvent(submission.Id, submission.ParticipantId, submission.ReceiptCode, submission.IsLate), cancellationToken);
        await realtime.PublishParticipantAsync(submission.SessionId, submission.ParticipantId, RealtimeEvents.ReceiptCreated, submission.Participant.Session.Sequence, new { submissionId = submission.Id, receiptCode = submission.ReceiptCode }, cancellationToken);
        return new FinalizeSubmissionResponse(submission.Status, receivedAt, submission.IsLate, submission.ReceiptCode, submission.ReceiptSignature, completedFiles);
    }

    public async Task<ReceiptDto> GetReceiptAsync(Guid submissionId, CancellationToken cancellationToken)
    {
        var submission = await db.SubmissionsSet.AsNoTracking().Include(x => x.Files).FirstOrDefaultAsync(x => x.Id == submissionId, cancellationToken) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy bài nộp.", 404);
        if (submission.ServerReceivedAtUtc is null || submission.ReceiptCode is null || submission.ReceiptSignature is null) throw new ApiException(ErrorCodes.Conflict, "Bài nộp chưa có biên nhận.", 409);
        return new ReceiptDto(submission.Id, submission.ReceiptCode, submission.ReceiptSignature, submission.ServerReceivedAtUtc.Value, submission.IsLate, submission.Files.Select(ToDescriptor).ToList());
    }

    public async Task<PagedResult<SubmissionSummaryDto>> ListForSessionAsync(Guid sessionId, SubmissionStatus? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 200);
        var query = db.SubmissionsSet.AsNoTracking().Include(x => x.Files).Include(x => x.Participant).Where(x => x.SessionId == sessionId);
        if (status.HasValue) query = query.Where(x => x.Status == status.Value);
        var total = await query.CountAsync(cancellationToken); var rows = await query.OrderBy(x => x.Participant.StudentCode).ThenByDescending(x => x.AttemptNumber).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new(rows.Select(ToSummary).ToList(), page, pageSize, total);
    }

    public async Task<SubmissionSummaryDto> GetAsync(Guid submissionId, CancellationToken cancellationToken)
    {
        var entity = await db.SubmissionsSet.AsNoTracking().Include(x => x.Files).Include(x => x.Participant).FirstOrDefaultAsync(x => x.Id == submissionId, cancellationToken) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy bài nộp.", 404);
        return ToSummary(entity);
    }

    public async Task RejectAsync(Guid submissionId, RejectSubmissionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new ApiException(ErrorCodes.ValidationFailed, "Phải có lý do từ chối.");
        var submission = await db.SubmissionsSet.Include(x => x.Participant).ThenInclude(x => x.Session).FirstOrDefaultAsync(x => x.Id == submissionId, cancellationToken) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy bài nộp.", 404);
        if (submission.Status is not (SubmissionStatus.Submitted or SubmissionStatus.LateSubmitted)) throw new ApiException(ErrorCodes.InvalidStateTransition, "Chỉ từ chối bài đã nộp.", 409);
        submission.Status = SubmissionStatus.Rejected; submission.TeacherRejectReason = request.Reason; submission.Participant.SubmissionStatus = SubmissionStatus.Rejected; submission.Participant.Session.Sequence++;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("SubmissionRejected", nameof(Submission), submission.Id.ToString(), submission.SessionId, null, request, cancellationToken);
        await outbox.EnqueueAsync(
            "submissions",
            submission.Id.ToString(),
            "upsert",
            ToCloud(submission),
            cancellationToken: cancellationToken);
        await realtime.PublishParticipantAsync(submission.SessionId, submission.ParticipantId, RealtimeEvents.SubmissionRejected, submission.Participant.Session.Sequence, new SubmissionRejectedEvent(submission.Id, request.Reason), cancellationToken);
    }

    public async Task AllowResubmitAsync(Guid participantId, AllowResubmitRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new ApiException(ErrorCodes.ValidationFailed, "Phải có lý do cho nộp lại.");
        var participant = await db.SessionParticipantsSet.FirstOrDefaultAsync(x => x.Id == participantId, cancellationToken) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy người tham gia.", 404);
        participant.ResubmitAllowed = true;
        participant.ResubmitReason = request.Reason;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            "ResubmitAllowed",
            nameof(SessionParticipant),
            participant.Id.ToString(),
            participant.SessionId,
            null,
            request,
            cancellationToken);
        await outbox.EnqueueAsync(
            "session_participants",
            participant.Id.ToString(),
            "upsert",
            ToCloud(participant),
            cancellationToken: cancellationToken);
    }

    public async Task<(string Path, string MimeType, string DownloadName)> GetFileAsync(Guid submissionId, Guid fileId, CancellationToken cancellationToken)
    {
        var file = await db.SubmissionFilesSet.AsNoTracking().FirstOrDefaultAsync(x => x.Id == fileId && x.SubmissionId == submissionId && x.TransferStatus == TransferStatus.Completed, cancellationToken) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy file.", 404);
        var full = Path.GetFullPath(Path.Combine(paths.RootPath, file.RelativePath));
        if (!full.StartsWith(Path.GetFullPath(paths.RootPath), StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) throw new ApiException(ErrorCodes.NotFound, "File vật lý không tồn tại.", 404);
        return (full, file.MimeType, file.OriginalName);
    }

    private InitSubmissionResponse ToInitResponse(Submission s) => new(s.Id, s.AttemptNumber, _options.Transfer.ChunkSizeBytes, s.Files.Select(f => new ChunkPlanDto(f.Id, f.TotalChunks, Enumerable.Range(0, f.TotalChunks).Except(chunks.ReadReceivedChunks(f.ReceivedChunksJson)).ToList())).ToList(), s.DeadlineUtc);
    private SubmissionSummaryDto ToSummary(Submission s) => new(s.Id, s.SessionId, s.ParticipantId, s.Participant.StudentCode, s.Participant.DisplayName, s.AttemptNumber, s.Status, s.ClientSubmittedAtUtc, s.ServerReceivedAtUtc, s.DeadlineUtc, s.IsLate, s.ReceiptCode, s.IsOfficial, s.Files.Select(f => f.ToDto(chunks.ReadReceivedChunks(f.ReceivedChunksJson))).ToList());
    private static FileDescriptorDto ToDescriptor(SubmissionFile f) => new(f.Id, f.OriginalName, f.SizeBytes, f.Sha256, f.MimeType, $"/api/v1/submissions/{f.SubmissionId}/files/{f.Id}/content");
    private static object ToCloud(Submission x) => new
    {
        id = x.Id,
        session_id = x.SessionId,
        participant_id = x.ParticipantId,
        attempt_number = x.AttemptNumber,
        idempotency_key = x.IdempotencyKey,
        status = x.Status.ToString(),
        client_submitted_at = x.ClientSubmittedAtUtc,
        server_received_at = x.ServerReceivedAtUtc,
        deadline_at = x.DeadlineUtc,
        is_late = x.IsLate,
        is_official = x.IsOfficial,
        receipt_code = x.ReceiptCode,
        receipt_signature = x.ReceiptSignature,
        teacher_reject_reason = x.TeacherRejectReason,
        client_note = x.ClientNote,
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc
    };

    private static object ToCloud(SessionParticipant x) => new
    {
        id = x.Id,
        session_id = x.SessionId,
        user_id = x.UserId,
        student_code = x.StudentCode,
        display_name = x.DisplayName,
        class_name = x.ClassName,
        device_id = x.DeviceId,
        machine_name = x.MachineName,
        ip_address = x.IpAddress,
        app_version = x.AppVersion,
        status = x.Status.ToString(),
        joined_at = x.JoinedAtUtc,
        approved_at = x.ApprovedAtUtc,
        last_seen_at = x.LastSeenUtc,
        download_status = x.DownloadStatus.ToString(),
        submission_status = x.SubmissionStatus.ToString(),
        extra_time_minutes = x.ExtraTimeMinutes,
        resubmit_allowed = x.ResubmitAllowed,
        resubmit_reason = x.ResubmitReason,
        capability_json = x.CapabilityJson,
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc
    };

    private static object ToCloud(SubmissionFile x) => new
    {
        id = x.Id,
        submission_id = x.SubmissionId,
        client_file_id = x.ClientFileId,
        name = x.OriginalName,
        stored_name = x.StoredName,
        size_bytes = x.SizeBytes,
        sha256 = x.Sha256,
        mime_type = x.MimeType,
        transfer_status = x.TransferStatus.ToString(),
        sync_status = x.SyncStatus.ToString(),
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc
    };

    private static void ValidateFiles(IReadOnlyList<InitSubmissionFileRequest> files)
    {
        if (files.Count != StudentSubmissionPolicy.MaxFileCount)
            throw new ApiException(ErrorCodes.SubmissionFileCountInvalid, "Bài nộp phải có đúng một file nén.");
        foreach (var f in files)
        {
            if (!StudentSubmissionPolicy.IsAllowedExtension(f.Name))
                throw new ApiException(ErrorCodes.SubmissionArchiveRequired, "Bài làm phải được nén thành một file .zip, .rar hoặc .7z trước khi nộp.");
            if (f.SizeBytes <= 0 || f.SizeBytes > StudentSubmissionPolicy.MaxBytes)
                throw new ApiException(ErrorCodes.SubmissionTooLarge, "File bài làm vượt quá 10 MB. Hãy xóa dữ liệu không cần thiết hoặc giảm dung lượng rồi nén lại.");
            if (f.Sha256.Length != 64 || !f.Sha256.All(Uri.IsHexDigit)) throw new ApiException(ErrorCodes.ValidationFailed, $"SHA-256 của {f.Name} không hợp lệ.");
        }
    }
}
