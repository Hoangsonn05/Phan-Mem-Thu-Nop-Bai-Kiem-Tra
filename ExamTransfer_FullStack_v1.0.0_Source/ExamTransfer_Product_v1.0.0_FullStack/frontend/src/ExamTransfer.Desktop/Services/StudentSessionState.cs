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

    public Guid? SessionId { get => sessionId; set => Set(ref sessionId, value); }
    public Guid? ParticipantId { get => participantId; set => Set(ref participantId, value); }
    public Guid? ExamId { get => examId; set => Set(ref examId, value); }
    public string? AccessToken { get => accessToken; set => Set(ref accessToken, value); }
    public string RoomCode { get => roomCode; set => Set(ref roomCode, value); }
    public string DisplayName { get => displayName; set => Set(ref displayName, value); }
    public string StudentCode { get => studentCode; set => Set(ref studentCode, value); }
    public Guid? LastSubmissionId { get => lastSubmissionId; set => Set(ref lastSubmissionId, value); }
    public ReceiptDto? LastReceipt { get => lastReceipt; set => Set(ref lastReceipt, value); }

    public bool HasSession => SessionId.HasValue && ParticipantId.HasValue;

    public void ApplyJoin(JoinSessionResponse response, string room, string code, string name)
    {
        SessionId = response.SessionId;
        ParticipantId = response.ParticipantId;
        AccessToken = response.AccessToken;
        RoomCode = room;
        StudentCode = code;
        DisplayName = name;
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
    }
}
