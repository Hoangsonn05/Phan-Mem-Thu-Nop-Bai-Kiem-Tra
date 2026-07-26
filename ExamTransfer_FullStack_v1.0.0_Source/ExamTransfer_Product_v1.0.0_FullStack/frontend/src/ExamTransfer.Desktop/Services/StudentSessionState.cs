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
    private string? serverId;
    private int examVersion = 1;
    private ExamDeliveryType deliveryType = ExamDeliveryType.FileSubmission;
    private SupervisionMode supervisionMode = SupervisionMode.None;
    private QuizResultPolicy resultPolicy = QuizResultPolicy.Hidden;
    private SessionStatus? sessionStatus;
    private ParticipantStatus? participantStatus;
    private SubmissionStatus submissionStatus = SubmissionStatus.NotStarted;
    private QuizAttemptDto? currentAttempt;
    private long revision;
    private string? routeIntent;

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
    public string? ServerId { get => serverId; set => Set(ref serverId, value); }
    public int ExamVersion { get => examVersion; set => Set(ref examVersion, value); }
    public ExamDeliveryType DeliveryType { get => deliveryType; set => Set(ref deliveryType, value); }
    public SupervisionMode SupervisionMode { get => supervisionMode; set => Set(ref supervisionMode, value); }
    public QuizResultPolicy ResultPolicy { get => resultPolicy; set => Set(ref resultPolicy, value); }
    public SessionStatus? SessionStatus { get => sessionStatus; set => Set(ref sessionStatus, value); }
    public ParticipantStatus? ParticipantStatus { get => participantStatus; set => Set(ref participantStatus, value); }
    public SubmissionStatus SubmissionStatus { get => submissionStatus; set => Set(ref submissionStatus, value); }
    public QuizAttemptDto? CurrentAttempt { get => currentAttempt; set => Set(ref currentAttempt, value); }
    public long Revision { get => revision; set => Set(ref revision, value); }
    public string? RouteIntent { get => routeIntent; set => Set(ref routeIntent, value); }

    public bool HasSession => SessionId.HasValue && ParticipantId.HasValue;
    public event EventHandler? SessionChanged;

    public void ApplyJoin(JoinSessionResponse response, string room, string code, string name, SessionAccessMode mode = SessionAccessMode.LanOnly, string? discoveredServerId = null)
    {
        SessionId = response.SessionId;
        ParticipantId = response.ParticipantId;
        AccessToken = response.AccessToken;
        RoomCode = room;
        StudentCode = code;
        DisplayName = name;
        AccessMode = mode;
        ServerId = discoveredServerId;
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

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
        ServerId = null;
        ExamVersion = 1;
        DeliveryType = ExamDeliveryType.FileSubmission;
        SupervisionMode = SupervisionMode.None;
        ResultPolicy = QuizResultPolicy.Hidden;
        SessionStatus = null;
        ParticipantStatus = null;
        SubmissionStatus = SubmissionStatus.NotStarted;
        CurrentAttempt = null;
        Revision = 0;
        RouteIntent = null;
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }
}
