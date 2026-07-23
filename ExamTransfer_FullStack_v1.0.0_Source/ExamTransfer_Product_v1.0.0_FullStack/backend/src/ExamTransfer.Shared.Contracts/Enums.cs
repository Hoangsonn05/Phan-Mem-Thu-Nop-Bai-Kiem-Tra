namespace ExamTransfer.Shared.Contracts;

public enum UserRole { Admin, Teacher, Student }
public enum ClassStatus { Active, Archived }
public enum ClassAccessMode { Private, Public }
public enum SessionAccessMode { LanOnly, PublicCloud }
public enum ExamStatus { Draft, Published, Archived, Cancelled }
public enum ExamDeliveryType { FileSubmission, MultipleChoice }
public enum QuizAttemptStatus { InProgress, Finalized }
public enum SessionStatus { Draft, Waiting, Distributing, InProgress, Paused, Collecting, Finished, Archived, Cancelled }
public enum ParticipantStatus { NotConnected, Connected, PendingApproval, Approved, Rejected, Disconnected }
public enum DownloadStatus { NotStarted, Queued, Downloading, Verifying, Completed, Failed }
public enum SubmissionStatus { NotStarted, Preparing, Uploading, Verifying, Submitted, LateSubmitted, Rejected, Failed }
public enum TransferStatus { Queued, Running, Paused, Retrying, Completed, Failed, Cancelled }
public enum SyncStatus { LocalOnly, Pending, Syncing, Synced, Conflict, Failed }
public enum GradingStatus { NotGraded, InProgress, Graded, Returned }
public enum ConnectionState { Offline, Connecting, Online, Reconnecting, Degraded }
public enum PolicyApplyStatus { NotRequested, Applying, Applied, Unsupported, Failed }
public enum ViolationSeverity { Info, Warning, Critical }
public enum ExportStatus { Queued, Running, Completed, Failed, Cancelled }
public enum BackupStatus { Creating, Ready, Invalid, RestorePending, Failed }
public enum MessageType { Information, Warning, TimeChange, System }
public enum TransferDirection { Download, Upload }
public enum ControlActionType { Warn, Unlock, EndDeviceSession, RequestExplanation }
public enum DeviceCommandType
{
    ApplyPolicy,
    UpdatePolicy,
    ShowWarning,
    LockExamApplication,
    UnlockExamApplication,
    ForceFocusExamApplication,
    RequestDeviceSnapshot,
    RequestRunningProcesses,
    ForceSubmit,
    EndDeviceSession,
    ClearPolicy
}
public enum DeviceCommandStatus { Pending, Received, Executed, Failed, Expired, Ignored }
