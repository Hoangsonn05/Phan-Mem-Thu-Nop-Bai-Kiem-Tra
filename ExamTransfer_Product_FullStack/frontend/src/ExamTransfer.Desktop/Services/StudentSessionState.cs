using ExamTransfer.Desktop.Core;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Services;

public sealed class StudentSessionState : ObservableObject
{
    private Guid? sessionId;
    private Guid? participantId;
    private Guid? examId;
    private string? accessToken;
    private string roomCode = string.Empty;
    private string displayName = string.Empty;
    private string studentCode = string.Empty;
    private Guid? lastSubmissionId;
    private ReceiptDto? lastReceipt;
    private SessionAccessMode accessMode = SessionAccessMode.LanOnly;
    private SessionAdmissionMode admissionMode = SessionAdmissionMode.ClassMembersOnly;
    private string? serverId;
    private string examTitle = string.Empty;
    private string subject = string.Empty;
    private int durationMinutes;
    private int examVersion = 1;
    private ExamDeliveryType deliveryType = ExamDeliveryType.FileSubmission;
    private SupervisionMode supervisionMode = SupervisionMode.None;
    private QuizResultPolicy resultPolicy = QuizResultPolicy.Hidden;
    private SessionStatus? sessionStatus;
    private ParticipantStatus? participantStatus;
    private SubmissionStatus submissionStatus = SubmissionStatus.NotStarted;
    private bool resubmitAllowed;
    private QuizAttemptDto? currentAttempt;
    private long revision;
    private string? routeIntent;
    private bool joinMutationCommitted;
    private bool postJoinSynchronizationPending;

    public Guid? SessionId { get => sessionId; set => Set(ref sessionId, value); }
    public Guid? ParticipantId { get => participantId; set => Set(ref participantId, value); }
    public Guid? ExamId { get => examId; set => Set(ref examId, value); }
    public string? AccessToken { get => accessToken; set => Set(ref accessToken, value); }
    public string RoomCode { get => roomCode; set => Set(ref roomCode, value); }
    public string DisplayName { get => displayName; set => Set(ref displayName, value); }
    public string StudentCode { get => studentCode; set => Set(ref studentCode, value); }
    public Guid? LastSubmissionId { get => lastSubmissionId; set => Set(ref lastSubmissionId, value); }
    public ReceiptDto? LastReceipt { get => lastReceipt; set => Set(ref lastReceipt, value); }
    public SessionAccessMode AccessMode { get => accessMode; set => Set(ref accessMode, value); }
    public SessionAdmissionMode AdmissionMode { get => admissionMode; set => Set(ref admissionMode, value); }
    public string? ServerId { get => serverId; set => Set(ref serverId, value); }
    public string ExamTitle { get => examTitle; set => Set(ref examTitle, value); }
    public string Subject { get => subject; set => Set(ref subject, value); }
    public int DurationMinutes { get => durationMinutes; set => Set(ref durationMinutes, value); }
    public int ExamVersion { get => examVersion; set => Set(ref examVersion, value); }
    public ExamDeliveryType DeliveryType { get => deliveryType; set => Set(ref deliveryType, value); }
    public SupervisionMode SupervisionMode { get => supervisionMode; set => Set(ref supervisionMode, value); }
    public QuizResultPolicy ResultPolicy { get => resultPolicy; set => Set(ref resultPolicy, value); }
    public SessionStatus? SessionStatus { get => sessionStatus; set => Set(ref sessionStatus, value); }
    public ParticipantStatus? ParticipantStatus { get => participantStatus; set => Set(ref participantStatus, value); }
    public SubmissionStatus SubmissionStatus { get => submissionStatus; set => Set(ref submissionStatus, value); }
    public bool ResubmitAllowed { get => resubmitAllowed; private set => Set(ref resubmitAllowed, value); }
    public QuizAttemptDto? CurrentAttempt { get => currentAttempt; set => Set(ref currentAttempt, value); }
    public long Revision { get => revision; set => Set(ref revision, value); }
    public string? RouteIntent { get => routeIntent; set => Set(ref routeIntent, value); }
    public bool JoinMutationCommitted
    {
        get => joinMutationCommitted;
        private set => Set(ref joinMutationCommitted, value);
    }
    public bool PostJoinSynchronizationPending
    {
        get => postJoinSynchronizationPending;
        private set => Set(ref postJoinSynchronizationPending, value);
    }

    public bool HasSession => SessionId.HasValue && ParticipantId.HasValue;
    public event EventHandler? SessionChanged;

    public void ApplyJoin(JoinSessionResponse response, string room, string code, string name, SessionAccessMode mode = SessionAccessMode.LanOnly, string? discoveredServerId = null)
        => ApplyJoin(
            response.SessionId,
            response.ParticipantId,
            response.AccessToken,
            room,
            code,
            name,
            mode,
            discoveredServerId);

    public void ApplyJoin(
        Guid joinedSessionId,
        Guid joinedParticipantId,
        string joinedAccessToken,
        string room,
        string code,
        string name,
        SessionAccessMode mode,
        string? discoveredServerId = null)
    {
        SessionId = joinedSessionId;
        ParticipantId = joinedParticipantId;
        AccessToken = joinedAccessToken;
        RoomCode = room;
        StudentCode = code;
        DisplayName = name;
        AccessMode = mode;
        ServerId = discoveredServerId;
        ApplyResubmitAuthority(false);
        JoinMutationCommitted = true;
        PostJoinSynchronizationPending = true;
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool CanResumePostJoinSynchronization(SessionAccessMode mode, string room) =>
        JoinMutationCommitted
        && PostJoinSynchronizationPending
        && HasSession
        && !string.IsNullOrWhiteSpace(AccessToken)
        && AccessMode == mode
        && string.Equals(
            RoomCodeRules.Normalize(RoomCode),
            RoomCodeRules.Normalize(room),
            StringComparison.OrdinalIgnoreCase);

    public void MarkPostJoinSynchronizationSucceeded() =>
        PostJoinSynchronizationPending = false;

    public void MarkPostJoinSynchronizationFailed()
    {
        if (JoinMutationCommitted && HasSession)
            PostJoinSynchronizationPending = true;
    }

    internal void ApplyResubmitAuthority(bool allowed) =>
        ResubmitAllowed = allowed;

    public void Reset()
    {
        SessionId = null;
        ParticipantId = null;
        ExamId = null;
        AccessToken = null;
        RoomCode = string.Empty;
        LastSubmissionId = null;
        LastReceipt = null;
        AccessMode = SessionAccessMode.LanOnly;
        AdmissionMode = SessionAdmissionMode.ClassMembersOnly;
        ServerId = null;
        ExamTitle = string.Empty;
        Subject = string.Empty;
        DurationMinutes = 0;
        ExamVersion = 1;
        DeliveryType = ExamDeliveryType.FileSubmission;
        SupervisionMode = SupervisionMode.None;
        ResultPolicy = QuizResultPolicy.Hidden;
        SessionStatus = null;
        ParticipantStatus = null;
        SubmissionStatus = SubmissionStatus.NotStarted;
        ApplyResubmitAuthority(false);
        CurrentAttempt = null;
        Revision = 0;
        RouteIntent = null;
        JoinMutationCommitted = false;
        PostJoinSynchronizationPending = false;
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }
}
