namespace ExamTransfer.Shared.Contracts;

public static class ExamDistributionAccessPolicy
{
    public static SessionStatus[] FileAccessStatuses =>
    [
        SessionStatus.Distributing,
        SessionStatus.InProgress,
        SessionStatus.Paused,
        SessionStatus.Collecting
    ];

    public static bool CanReceiveFile(
        ParticipantStatus? participantStatus,
        SessionStatus? sessionStatus,
        ExamDeliveryType deliveryType) =>
        participantStatus == ParticipantStatus.Approved
        && deliveryType == ExamDeliveryType.FileSubmission
        && sessionStatus is SessionStatus.Distributing
            or SessionStatus.InProgress
            or SessionStatus.Paused
            or SessionStatus.Collecting;

    public static bool CanAccessQuiz(
        ParticipantStatus? participantStatus,
        SessionStatus? sessionStatus,
        ExamDeliveryType deliveryType) =>
        participantStatus == ParticipantStatus.Approved
        && deliveryType == ExamDeliveryType.MultipleChoice
        && sessionStatus is SessionStatus.InProgress
            or SessionStatus.Paused
            or SessionStatus.Collecting;
}
