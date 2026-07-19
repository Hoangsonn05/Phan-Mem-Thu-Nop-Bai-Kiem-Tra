using System.Security.Cryptography;
using System.Text.Json;
using ExamTransfer.Application;
using ExamTransfer.Domain;
using ExamTransfer.Infrastructure.Persistence;
using ExamTransfer.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ExamTransfer.Infrastructure.Services;

public sealed class SessionService(AppDbContext db, ISessionTokenService tokens, IAuditService audit, IOutboxService outbox, IRealtimePublisher realtime, IOptions<ExamTransferOptions> options) : ISessionService
{
    private readonly ExamTransferOptions _options = options.Value;

    public async Task<PagedResult<SessionSummaryDto>> ListAsync(SessionStatus? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 200);
        var query = db.ExamSessionsSet.AsNoTracking().Include(x => x.Exam).Include(x => x.Participants).AsQueryable();
        if (status.HasValue) query = query.Where(x => x.Status == status.Value);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(x => x.UpdatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new(rows.Select(ToSummary).ToList(), page, pageSize, total);
    }

    public async Task<SessionDetailDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var session = await db.ExamSessionsSet.AsNoTracking().Include(x => x.Exam).Include(x => x.Participants).FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy phòng thi.", 404);
        return ToDetail(session);
    }

    public async Task<SessionDetailDto> CreateAsync(CreateSessionRequest request, string hostDeviceId, CancellationToken cancellationToken)
    {
        var exam = await db.ExamsSet.FirstOrDefaultAsync(x => x.Id == request.ExamId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy bài kiểm tra.", 404);
        if (exam.Status != ExamStatus.Published) throw new ApiException(ErrorCodes.InvalidStateTransition, "Chỉ tạo phòng từ bài kiểm tra đã phát hành.", 409);
        ValidateSessionConfiguration(request.SettingsJson, request.Capacity);
        if (request.ClassId.HasValue && !await db.ClassesSet.AnyAsync(x => x.Id == request.ClassId.Value, cancellationToken))
            throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy lớp được chọn.", 404);
        var roomCode = string.IsNullOrWhiteSpace(request.CustomRoomCode) ? await GenerateRoomCodeAsync(cancellationToken) : request.CustomRoomCode.Trim().ToUpperInvariant();
        if (roomCode.Length < 4 || roomCode.Length > 12) throw new ApiException(ErrorCodes.ValidationFailed, "Mã phòng phải dài 4-12 ký tự.");
        if (await db.ExamSessionsSet.AnyAsync(x => x.RoomCode == roomCode && x.Status != SessionStatus.Archived && x.Status != SessionStatus.Cancelled && x.Status != SessionStatus.Finished, cancellationToken))
            throw new ApiException(ErrorCodes.RoomCodeConflict, "Mã phòng đang được sử dụng.", 409);
        var session = new ExamSession
        {
            ExamId = request.ExamId, ClassId = request.ClassId ?? exam.ClassId, RoomCode = roomCode,
            HostDeviceId = hostDeviceId, PlannedStartUtc = request.PlannedStartUtc, SettingsJson = string.IsNullOrWhiteSpace(request.SettingsJson) ? "{}" : request.SettingsJson,
            AutoApprove = request.AutoApprove, Capacity = request.Capacity, Status = SessionStatus.Draft, AcceptingParticipants = true
        };
        db.ExamSessionsSet.Add(session); await db.SaveChangesAsync(cancellationToken);
        session.Exam = exam;
        await audit.WriteAsync("SessionCreated", nameof(ExamSession), session.Id.ToString(), session.Id, null, session, cancellationToken);
        await outbox.EnqueueAsync("exam_sessions", session.Id.ToString(), "upsert", ToCloud(session), cancellationToken: cancellationToken);
        return ToDetail(session);
    }

    public async Task<SessionDetailDto> UpdateAsync(Guid id, UpdateSessionRequest request, CancellationToken cancellationToken)
    {
        var session = await db.ExamSessionsSet.Include(x => x.Exam).Include(x => x.Participants).FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy phòng thi.", 404);
        if (session.Status != SessionStatus.Draft) throw new ApiException(ErrorCodes.InvalidStateTransition, "Chỉ sửa phòng ở trạng thái Draft.", 409);
        EnsureRowVersion(session.RowVersion, request.RowVersion);
        ValidateSessionConfiguration(request.SettingsJson, request.Capacity);
        session.PlannedStartUtc = request.PlannedStartUtc;
        session.SettingsJson = string.IsNullOrWhiteSpace(request.SettingsJson) ? "{}" : request.SettingsJson;
        session.AutoApprove = request.AutoApprove;
        session.Capacity = request.Capacity;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("SessionUpdated", nameof(ExamSession), session.Id.ToString(), session.Id, null, session, cancellationToken);
        await outbox.EnqueueAsync(
            "exam_sessions",
            session.Id.ToString(),
            "upsert",
            ToCloud(session),
            cancellationToken: cancellationToken);
        return ToDetail(session);
    }

    public async Task<SessionDetailDto> TransitionAsync(Guid id, SessionStatus target, EndSessionRequest? endRequest, CancellationToken cancellationToken)
    {
        var session = await db.ExamSessionsSet.Include(x => x.Exam).Include(x => x.Participants).FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy phòng thi.", 404);
        if (target is SessionStatus.Finished or SessionStatus.Cancelled)
        {
            var activeUploads = await db.SubmissionsSet.AnyAsync(x => x.SessionId == id && (x.Status == SubmissionStatus.Uploading || x.Status == SubmissionStatus.Verifying), cancellationToken);
            if (activeUploads && endRequest?.Force != true) throw new ApiException(ErrorCodes.Conflict, "Đang có bài nộp upload; cần force=true và lý do để kết thúc.", 409);
            if (endRequest?.Force == true && string.IsNullOrWhiteSpace(endRequest.Reason)) throw new ApiException(ErrorCodes.ValidationFailed, "Kết thúc cưỡng bức phải có lý do.");
        }
        var before = session.Status;
        session.TransitionTo(target);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("SessionStateChanged", nameof(ExamSession), session.Id.ToString(), session.Id, new { status = before }, new { status = session.Status, reason = endRequest?.Reason }, cancellationToken);
        await outbox.EnqueueAsync("exam_sessions", session.Id.ToString(), "upsert", ToCloud(session), cancellationToken: cancellationToken);
        await realtime.PublishSessionAsync(session.Id, RealtimeEvents.SessionStateChanged, session.Sequence, new SessionStateChangedEvent(session.Status, DateTimeOffset.UtcNow, EffectiveDeadline(session)), cancellationToken);
        return ToDetail(session);
    }

    public async Task<JoinSessionResponse> JoinAsync(JoinSessionRequest request, Guid accountUserId, string studentCode, string displayName, string? ipAddress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RoomCode) || string.IsNullOrWhiteSpace(studentCode) || string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(request.DeviceId))
            throw new ApiException(ErrorCodes.ValidationFailed, "Mã phòng, danh tính tài khoản và Device ID là bắt buộc.");
        if (!string.IsNullOrWhiteSpace(request.StudentCode) && !request.StudentCode.Trim().Equals(studentCode.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ApiException(ErrorCodes.ParticipantAccountMismatch, "Mã sinh viên trong yêu cầu không khớp với tài khoản đăng nhập.", 403);
        if (!string.IsNullOrWhiteSpace(request.DisplayName) && !request.DisplayName.Trim().Equals(displayName.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ApiException(ErrorCodes.ParticipantAccountMismatch, "Họ tên trong yêu cầu không khớp với tài khoản đăng nhập.", 403);
        var roomCode = request.RoomCode.Trim().ToUpperInvariant();
        var session = await db.ExamSessionsSet.Include(x => x.Exam).Include(x => x.Participants).FirstOrDefaultAsync(x => x.RoomCode == roomCode, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy phòng thi.", 404);
        if (session.Status != SessionStatus.Waiting || !session.AcceptingParticipants) throw new ApiException(ErrorCodes.InvalidStateTransition, "Phòng chưa mở hoặc đã khóa nhận người mới.", 409);
        if (session.Capacity.HasValue && session.Participants.Count >= session.Capacity.Value) throw new ApiException(ErrorCodes.Conflict, "Phòng đã đủ số lượng.", 409);
        var existing = session.Participants.FirstOrDefault(x => x.StudentCode.Equals(studentCode.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (existing.UserId.HasValue && existing.UserId.Value != accountUserId)
                throw new ApiException(ErrorCodes.ParticipantAccountMismatch, "Lượt tham gia không thuộc tài khoản đang đăng nhập.", 403);
            if (!existing.DeviceId.Equals(request.DeviceId, StringComparison.Ordinal)) throw new ApiException(ErrorCodes.DuplicateStudentCode, "Mã học sinh đang được dùng trên thiết bị khác.", 409);
            existing.UserId = accountUserId;
            existing.DisplayName = displayName.Trim();
            existing.LastSeenUtc = DateTimeOffset.UtcNow; existing.IpAddress = ipAddress; existing.MachineName = request.MachineName; existing.AppVersion = request.AppVersion;
            if (existing.Status == ParticipantStatus.Disconnected) existing.Status = ParticipantStatus.Connected;
            await db.SaveChangesAsync(cancellationToken);
            await outbox.EnqueueAsync(
                "session_participants",
                existing.Id.ToString(),
                "upsert",
                ToCloud(existing),
                cancellationToken: cancellationToken);
            return CreateJoinResponse(session, existing);
        }
        var participant = new SessionParticipant
        {
            SessionId = session.Id, UserId = accountUserId, StudentCode = studentCode.Trim(), DisplayName = displayName.Trim(), ClassName = request.ClassName?.Trim(),
            DeviceId = request.DeviceId, MachineName = request.MachineName, IpAddress = ipAddress, AppVersion = request.AppVersion,
            Status = session.AutoApprove ? ParticipantStatus.Approved : ParticipantStatus.PendingApproval,
            ApprovedAtUtc = session.AutoApprove ? DateTimeOffset.UtcNow : null, LastSeenUtc = DateTimeOffset.UtcNow
        };
        db.SessionParticipantsSet.Add(participant); session.Sequence++;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("ParticipantJoined", nameof(SessionParticipant), participant.Id.ToString(), session.Id, null, participant, cancellationToken);
        await outbox.EnqueueAsync(
            "session_participants",
            participant.Id.ToString(),
            "upsert",
            ToCloud(participant),
            cancellationToken: cancellationToken);
        await realtime.PublishSessionAsync(session.Id, RealtimeEvents.ParticipantJoined, session.Sequence, participant.ToDto(DateTimeOffset.UtcNow, _options.Session.DisconnectAfterSeconds), cancellationToken);
        return CreateJoinResponse(session, participant);
    }

    public async Task<ParticipantDto> ApproveAsync(Guid sessionId, Guid participantId, CancellationToken cancellationToken)
    {
        var participant = await db.SessionParticipantsSet
            .Include(x => x.Session)
            .ThenInclude(x => x.Exam)
            .FirstOrDefaultAsync(x => x.Id == participantId && x.SessionId == sessionId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy người tham gia.", 404);
        if (participant.Session.Status != SessionStatus.Waiting) throw new ApiException(ErrorCodes.InvalidStateTransition, "Chỉ duyệt trong phòng chờ.", 409);
        participant.Status = ParticipantStatus.Approved; participant.ApprovedAtUtc = DateTimeOffset.UtcNow; participant.Session.Sequence++;
        await db.SaveChangesAsync(cancellationToken);
        var issued = tokens.IssueParticipantToken(
            sessionId,
            participantId,
            participant.UserId ?? Guid.Empty,
            participant.DeviceId,
            participant.Status,
            ParticipantTokenLifetime(participant.Session));
        await audit.WriteAsync("ParticipantApproved", nameof(SessionParticipant), participant.Id.ToString(), sessionId, null, participant, cancellationToken);
        await outbox.EnqueueAsync(
            "session_participants",
            participant.Id.ToString(),
            "upsert",
            ToCloud(participant),
            cancellationToken: cancellationToken);
        await realtime.PublishParticipantAsync(sessionId, participantId, RealtimeEvents.ParticipantApproved, participant.Session.Sequence, new ParticipantApprovedEvent(participant.Id, issued.ExpiresAtUtc), cancellationToken);
        return participant.ToDto(DateTimeOffset.UtcNow, _options.Session.DisconnectAfterSeconds);
    }

    public async Task RejectAsync(Guid sessionId, Guid participantId, string? reason, CancellationToken cancellationToken)
    {
        var participant = await db.SessionParticipantsSet.Include(x => x.Session).FirstOrDefaultAsync(x => x.Id == participantId && x.SessionId == sessionId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy người tham gia.", 404);
        participant.Status = ParticipantStatus.Rejected; participant.Session.Sequence++;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("ParticipantRejected", nameof(SessionParticipant), participant.Id.ToString(), sessionId, null, new { participant, reason }, cancellationToken);
        await outbox.EnqueueAsync(
            "session_participants",
            participant.Id.ToString(),
            "upsert",
            ToCloud(participant),
            cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<ParticipantDto>> BulkApproveAsync(Guid sessionId, BulkApproveRequest request, CancellationToken cancellationToken)
    {
        var requestedIds = request.ParticipantIds.Distinct().ToList();
        if (requestedIds.Count == 0)
            throw new ApiException(ErrorCodes.ValidationFailed, "Cần chọn ít nhất một học sinh để duyệt.");

        var session = await db.ExamSessionsSet
            .Include(x => x.Exam)
            .Include(x => x.Participants)
            .FirstOrDefaultAsync(x => x.Id == sessionId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy phòng thi.", 404);

        if (session.Status != SessionStatus.Waiting)
            throw new ApiException(ErrorCodes.InvalidStateTransition, "Chỉ duyệt học sinh trong phòng chờ.", 409);

        var participants = session.Participants
            .Where(x => requestedIds.Contains(x.Id))
            .ToList();

        if (participants.Count != requestedIds.Count)
        {
            var found = participants.Select(x => x.Id).ToHashSet();
            var missing = requestedIds.Where(x => !found.Contains(x)).ToList();
            throw new ApiException(
                ErrorCodes.NotFound,
                "Một hoặc nhiều học sinh không còn tồn tại trong phòng chờ.",
                404,
                details: new { missingParticipantIds = missing });
        }

        var invalid = participants
            .Where(x => x.Status == ParticipantStatus.Rejected)
            .Select(x => x.Id)
            .ToList();
        if (invalid.Count > 0)
            throw new ApiException(
                ErrorCodes.InvalidStateTransition,
                "Không thể duyệt hàng loạt học sinh đã bị từ chối.",
                409,
                details: new { participantIds = invalid });

        var events = new List<(SessionParticipant Participant, long Sequence, IssuedToken Token)>();
        foreach (var participant in participants)
        {
            if (participant.Status == ParticipantStatus.Approved)
                continue;

            participant.Status = ParticipantStatus.Approved;
            participant.ApprovedAtUtc = DateTimeOffset.UtcNow;
            session.Sequence++;
            var issued = tokens.IssueParticipantToken(
                sessionId,
                participant.Id,
                participant.UserId ?? Guid.Empty,
                participant.DeviceId,
                participant.Status,
                ParticipantTokenLifetime(session));
            events.Add((participant, session.Sequence, issued));
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var item in events)
        {
            await outbox.EnqueueAsync(
                "session_participants",
                item.Participant.Id.ToString(),
                "upsert",
                ToCloud(item.Participant),
                cancellationToken: cancellationToken);
            await realtime.PublishParticipantAsync(
                sessionId,
                item.Participant.Id,
                RealtimeEvents.ParticipantApproved,
                item.Sequence,
                new ParticipantApprovedEvent(item.Participant.Id, item.Token.ExpiresAtUtc),
                cancellationToken);
        }

        await audit.WriteAsync(
            "ParticipantsBulkApproved",
            nameof(SessionParticipant),
            null,
            sessionId,
            null,
            new { ids = requestedIds, approvedCount = events.Count },
            cancellationToken);

        return participants
            .Select(x => x.ToDto(DateTimeOffset.UtcNow, _options.Session.DisconnectAfterSeconds))
            .ToList();
    }

    public async Task<ParticipantDto> AddExtraTimeAsync(Guid sessionId, Guid participantId, ExtraTimeRequest request, CancellationToken cancellationToken)
    {
        if (request.Minutes <= 0 || string.IsNullOrWhiteSpace(request.Reason)) throw new ApiException(ErrorCodes.ValidationFailed, "Số phút và lý do là bắt buộc.");
        var participant = await db.SessionParticipantsSet.Include(x => x.Session).ThenInclude(x => x.Exam).FirstOrDefaultAsync(x => x.Id == participantId && x.SessionId == sessionId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy người tham gia.", 404);
        if (participant.Session.Status is not (SessionStatus.InProgress or SessionStatus.Paused)) throw new ApiException(ErrorCodes.InvalidStateTransition, "Chỉ cộng giờ khi phòng đang thi hoặc tạm dừng.", 409);
        participant.ExtraTimeMinutes += request.Minutes; participant.Session.Sequence++;
        db.ParticipantExtraTimesSet.Add(new ParticipantExtraTime { ParticipantId = participantId, Minutes = request.Minutes, Reason = request.Reason });
        await db.SaveChangesAsync(cancellationToken);
        var deadline = participant.Session.StartedAtUtc!.Value.AddMinutes(participant.Session.Exam.DurationMinutes + participant.ExtraTimeMinutes);
        await audit.WriteAsync("ParticipantExtraTimeAdded", nameof(SessionParticipant), participant.Id.ToString(), sessionId, null, request, cancellationToken);
        await outbox.EnqueueAsync(
            "session_participants",
            participant.Id.ToString(),
            "upsert",
            ToCloud(participant),
            cancellationToken: cancellationToken);
        await realtime.PublishSessionAsync(sessionId, RealtimeEvents.TimeExtended, participant.Session.Sequence, new TimeExtendedEvent(participantId, request.Minutes, deadline), cancellationToken);
        return participant.ToDto(DateTimeOffset.UtcNow, _options.Session.DisconnectAfterSeconds);
    }

    public async Task<MessageDto> SendMessageAsync(Guid sessionId, SendMessageRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Content) || request.Content.Length > 2000) throw new ApiException(ErrorCodes.ValidationFailed, "Nội dung thông báo không hợp lệ.");
        var session = await db.ExamSessionsSet.FirstOrDefaultAsync(x => x.Id == sessionId, cancellationToken) ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy phòng thi.", 404);
        var message = new Message { SessionId = sessionId, ReceiverId = request.ReceiverParticipantId, Type = request.Type, Content = request.Content.Trim() };
        db.MessagesSet.Add(message); session.Sequence++; await db.SaveChangesAsync(cancellationToken);
        var dto = new MessageDto(message.Id, message.SessionId, message.SenderId, message.ReceiverId, message.Type, message.Content, message.CreatedAtUtc);
        if (request.ReceiverParticipantId.HasValue) await realtime.PublishParticipantAsync(sessionId, request.ReceiverParticipantId.Value, RealtimeEvents.TeacherMessageReceived, session.Sequence, new TeacherMessageEvent(message.Id, message.Content, request.ReceiverParticipantId), cancellationToken);
        else await realtime.PublishSessionAsync(sessionId, RealtimeEvents.TeacherMessageReceived, session.Sequence, new TeacherMessageEvent(message.Id, message.Content, null), cancellationToken);
        return dto;
    }

    public async Task HeartbeatAsync(Guid sessionId, Guid participantId, string deviceId, HeartbeatRequest request, CancellationToken cancellationToken)
    {
        var participant = await db.SessionParticipantsSet.Include(x => x.Session).FirstOrDefaultAsync(x => x.Id == participantId && x.SessionId == sessionId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy người tham gia.", 404);
        if (!participant.DeviceId.Equals(deviceId, StringComparison.Ordinal)) throw new ApiException(ErrorCodes.Forbidden, "Token không thuộc thiết bị này.", 403);
        var wasDisconnected = participant.Status == ParticipantStatus.Disconnected;
        participant.LastSeenUtc = DateTimeOffset.UtcNow;
        if (wasDisconnected) participant.Status = participant.ApprovedAtUtc.HasValue ? ParticipantStatus.Approved : ParticipantStatus.Connected;
        if (wasDisconnected) participant.Session.Sequence++;
        await db.SaveChangesAsync(cancellationToken);
        if (wasDisconnected)
        {
            await realtime.PublishSessionAsync(sessionId, RealtimeEvents.ParticipantConnectionChanged, participant.Session.Sequence, new ParticipantConnectionChangedEvent(participantId, ConnectionState.Online, participant.LastSeenUtc.Value), cancellationToken);
        }
    }

    public async Task<ParticipantDto> GetParticipantAsync(Guid sessionId, Guid participantId, CancellationToken cancellationToken)
    {
        var entity = await db.SessionParticipantsSet.AsNoTracking().FirstOrDefaultAsync(x => x.Id == participantId && x.SessionId == sessionId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "Không tìm thấy người tham gia.", 404);
        return entity.ToDto(DateTimeOffset.UtcNow, _options.Session.DisconnectAfterSeconds);
    }

    private JoinSessionResponse CreateJoinResponse(ExamSession session, SessionParticipant participant)
    {
        var issued = tokens.IssueParticipantToken(
            session.Id,
            participant.Id,
            participant.UserId ?? Guid.Empty,
            participant.DeviceId,
            participant.Status,
            ParticipantTokenLifetime(session));
        return new JoinSessionResponse(session.Id, participant.Id, participant.Status, issued.Token, issued.ExpiresAtUtc, participant.ToDto(DateTimeOffset.UtcNow, _options.Session.DisconnectAfterSeconds));
    }

    private SessionDetailDto ToDetail(ExamSession session) => new(ToSummary(session), session.Participants.OrderBy(x => x.StudentCode).Select(x => x.ToDto(DateTimeOffset.UtcNow, _options.Session.DisconnectAfterSeconds)).ToList(), session.SettingsJson);
    private SessionSummaryDto ToSummary(ExamSession s)
    {
        var p = s.Participants; var now = DateTimeOffset.UtcNow;
        var counts = new SessionCountsDto(p.Count, p.Count(x => x.Status == ParticipantStatus.PendingApproval), p.Count(x => x.Status == ParticipantStatus.Approved), p.Count(x => x.LastSeenUtc.HasValue && now - x.LastSeenUtc <= TimeSpan.FromSeconds(_options.Session.DisconnectAfterSeconds)), p.Count(x => x.SubmissionStatus is SubmissionStatus.Submitted or SubmissionStatus.LateSubmitted), p.Count(x => x.SubmissionStatus == SubmissionStatus.Uploading), p.Count(x => x.Status == ParticipantStatus.Disconnected));
        return new SessionSummaryDto(s.Id, s.ExamId, s.Exam.Title, s.RoomCode, s.Status, now, s.StartedAtUtc, s.EndedAtUtc, EffectiveDeadline(s), counts, s.Sequence, s.RowVersion);
    }
    private static DateTimeOffset? EffectiveDeadline(ExamSession s) => s.StartedAtUtc?.AddMinutes(s.Exam.DurationMinutes);
    private async Task<string> GenerateRoomCodeAsync(CancellationToken cancellationToken)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var bytes = RandomNumberGenerator.GetBytes(_options.Security.RoomCodeLength); var code = new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
            if (!await db.ExamSessionsSet.AnyAsync(x => x.RoomCode == code && x.Status != SessionStatus.Archived && x.Status != SessionStatus.Cancelled && x.Status != SessionStatus.Finished, cancellationToken)) return code;
        }
        throw new ApiException(ErrorCodes.RoomCodeConflict, "Không thể sinh mã phòng không trùng.", 500);
    }
    private TimeSpan ParticipantTokenLifetime(ExamSession session)
    {
        var minimumMinutes = Math.Max(60, _options.Security.TokenMinutes);
        var examMinutes = Math.Max(1, session.Exam.DurationMinutes);
        return TimeSpan.FromMinutes(Math.Max(minimumMinutes, examMinutes + 180));
    }

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

    private static object ToCloud(ExamSession x) => new
    {
        id = x.Id,
        exam_id = x.ExamId,
        class_id = x.ClassId,
        room_code = x.RoomCode,
        status = x.Status.ToString(),
        host_device_id = x.HostDeviceId,
        planned_start_at = x.PlannedStartUtc,
        started_at = x.StartedAtUtc,
        ended_at = x.EndedAtUtc,
        settings_json = x.SettingsJson,
        auto_approve = x.AutoApprove,
        capacity = x.Capacity,
        accepting_participants = x.AcceptingParticipants,
        sequence = x.Sequence,
        created_at = x.CreatedAtUtc,
        updated_at = x.UpdatedAtUtc
    };

    private static void ValidateSessionConfiguration(string? settingsJson, int? capacity)
    {
        if (capacity.HasValue && (capacity.Value <= 0 || capacity.Value > 5000))
            throw new ApiException(ErrorCodes.ValidationFailed, "Sức chứa phòng phải nằm trong khoảng 1-5000.");

        if (string.IsNullOrWhiteSpace(settingsJson))
            return;

        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException();
        }
        catch (JsonException)
        {
            throw new ApiException(ErrorCodes.ValidationFailed, "Cấu hình phòng thi phải là JSON object hợp lệ.");
        }
    }

    private static void EnsureRowVersion(string current, string supplied) { if (current != supplied) throw new ApiException(ErrorCodes.ConcurrencyConflict, "Dữ liệu đã thay đổi.", 409, details: new { currentRowVersion = current }); }
}
