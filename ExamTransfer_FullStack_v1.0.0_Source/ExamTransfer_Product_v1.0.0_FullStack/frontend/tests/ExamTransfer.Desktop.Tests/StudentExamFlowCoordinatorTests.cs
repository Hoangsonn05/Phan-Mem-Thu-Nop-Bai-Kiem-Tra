using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class StudentExamFlowCoordinatorTests
{
    public static TheoryData<StudentExamFlowSnapshot, StudentExamFlowState, string, bool> Routes => new()
    {
        { Snapshot(false), StudentExamFlowState.NoSession, "S-01", false },
        { Snapshot(participant: ParticipantStatus.PendingApproval), StudentExamFlowState.PendingApproval, "S-03", false },
        { Snapshot(participant: ParticipantStatus.Rejected), StudentExamFlowState.RejectedOrExpired, "S-01", false },
        { Snapshot(status: SessionStatus.Waiting), StudentExamFlowState.ApprovedWaiting, "S-04", false },
        { Snapshot(delivery: ExamDeliveryType.FileSubmission), StudentExamFlowState.ReadyToStartFileExam, "S-05", false },
        { Snapshot(delivery: ExamDeliveryType.FileSubmission, submission: SubmissionStatus.Uploading), StudentExamFlowState.InProgressFileExam, "S-07", false },
        { Snapshot(delivery: ExamDeliveryType.FileSubmission, submission: SubmissionStatus.Submitted), StudentExamFlowState.SubmittedFileExam, "S-08", false },
        { Snapshot(delivery: ExamDeliveryType.MultipleChoice), StudentExamFlowState.ReadyToStartQuiz, "S-06", true },
        { Snapshot(delivery: ExamDeliveryType.MultipleChoice, attempt: QuizAttemptStatus.InProgress), StudentExamFlowState.InProgressQuiz, "S-06", false },
        { Snapshot(delivery: ExamDeliveryType.MultipleChoice, attempt: QuizAttemptStatus.Finalized), StudentExamFlowState.FinalizedQuiz, "S-06", false },
        { Snapshot(status: SessionStatus.Finished), StudentExamFlowState.SessionFinished, "S-04", false }
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public void ResolveSnapshot_UsesOneStateMachineForEveryEntryPoint(
        StudentExamFlowSnapshot snapshot,
        StudentExamFlowState expectedState,
        string expectedRoute,
        bool expectedConfirmation)
    {
        var currentExam = StudentExamFlowCoordinator.ResolveSnapshot(snapshot);
        var quizTab = StudentExamFlowCoordinator.ResolveSnapshot(snapshot);

        Assert.Equal(expectedState, currentExam.State);
        Assert.Equal(expectedRoute, currentExam.RouteKey);
        Assert.Equal(expectedConfirmation, currentExam.RequiresStartConfirmation);
        Assert.Equal(currentExam, quizTab);
    }

    private static StudentExamFlowSnapshot Snapshot(
        bool hasSession = true,
        SessionStatus status = SessionStatus.InProgress,
        ParticipantStatus participant = ParticipantStatus.Approved,
        ExamDeliveryType delivery = ExamDeliveryType.FileSubmission,
        SubmissionStatus submission = SubmissionStatus.NotStarted,
        QuizAttemptStatus? attempt = null) =>
        new(hasSession, status, participant, delivery, submission, attempt);
}
