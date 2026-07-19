namespace ExamTransfer.Shared.Contracts;

public sealed record RealtimeEnvelope<T>(Guid EventId, Guid SessionId, long Sequence, DateTimeOffset OccurredAtUtc, string EventType, T Payload);

public static class RealtimeEvents
{
    public const string SessionStateChanged = nameof(SessionStateChanged);
    public const string ParticipantJoined = nameof(ParticipantJoined);
    public const string ParticipantApproved = nameof(ParticipantApproved);
    public const string ParticipantConnectionChanged = nameof(ParticipantConnectionChanged);
    public const string ExamPublished = nameof(ExamPublished);
    public const string ExamUpdated = nameof(ExamUpdated);
    public const string DownloadProgressChanged = nameof(DownloadProgressChanged);
    public const string TransferProgressChanged = nameof(TransferProgressChanged);
    public const string SubmissionStarted = nameof(SubmissionStarted);
    public const string SubmissionAccepted = nameof(SubmissionAccepted);
    public const string SubmissionRejected = nameof(SubmissionRejected);
    public const string ReceiptCreated = nameof(ReceiptCreated);
    public const string TimeExtended = nameof(TimeExtended);
    public const string TeacherMessageReceived = nameof(TeacherMessageReceived);
    public const string ForceSubmitRequested = nameof(ForceSubmitRequested);
    public const string ViolationDetected = nameof(ViolationDetected);
    public const string ControlPolicyChanged = nameof(ControlPolicyChanged);
    public const string GradeReturned = nameof(GradeReturned);
    public const string ExportProgressChanged = nameof(ExportProgressChanged);
    public const string CloudSyncStatusChanged = nameof(CloudSyncStatusChanged);
    public const string BackupProgressChanged = nameof(BackupProgressChanged);
    public const string SettingsChanged = nameof(SettingsChanged);
    public const string SystemStatusChanged = nameof(SystemStatusChanged);
    public const string DashboardMetricChanged = nameof(DashboardMetricChanged);
}

public sealed record SessionStateChangedEvent(SessionStatus Status, DateTimeOffset ServerNowUtc, DateTimeOffset? EffectiveDeadlineUtc);
public sealed record ParticipantApprovedEvent(Guid ParticipantId, DateTimeOffset TokenExpiryUtc);
public sealed record ParticipantConnectionChangedEvent(Guid ParticipantId, ConnectionState ConnectionState, DateTimeOffset LastSeenUtc);
public sealed record DownloadProgressEvent(Guid ParticipantId, double Percent, long Bytes, DownloadStatus Status);
public sealed record TransferProgressEvent(Guid TransferId, TransferDirection Direction, long Bytes, double BytesPerSecond, TransferStatus Status);
public sealed record SubmissionAcceptedEvent(Guid SubmissionId, Guid ParticipantId, string ReceiptCode, bool IsLate);
public sealed record SubmissionRejectedEvent(Guid SubmissionId, string Reason);
public sealed record TimeExtendedEvent(Guid? ParticipantId, int Minutes, DateTimeOffset EffectiveDeadlineUtc);
public sealed record TeacherMessageEvent(Guid MessageId, string Content, Guid? TargetParticipantId);
public sealed record ForceSubmitEvent(DateTimeOffset DeadlineUtc, string Reason);
public sealed record ControlPolicyChangedEvent(int Version, PolicyApplyStatus ApplyStatus);
public sealed record GradeReturnedEvent(Guid SubmissionId, decimal? Score, decimal MaxScore);
public sealed record ExportProgressEvent(Guid JobId, double Progress, ExportStatus Status);
