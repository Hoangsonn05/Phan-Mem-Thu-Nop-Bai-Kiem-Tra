using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Domain;

public abstract class EntityBase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string RowVersion { get; set; } = Guid.NewGuid().ToString("N");
}

public sealed class DomainRuleException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class SessionStateMachine
{
    private static readonly IReadOnlyDictionary<SessionStatus, HashSet<SessionStatus>> Allowed =
        new Dictionary<SessionStatus, HashSet<SessionStatus>>
        {
            [SessionStatus.Draft] = [SessionStatus.Waiting, SessionStatus.Cancelled],
            [SessionStatus.Waiting] = [SessionStatus.Distributing, SessionStatus.InProgress, SessionStatus.Cancelled],
            [SessionStatus.Distributing] = [SessionStatus.InProgress, SessionStatus.Cancelled],
            [SessionStatus.InProgress] = [SessionStatus.Paused, SessionStatus.Collecting, SessionStatus.Finished, SessionStatus.Cancelled],
            [SessionStatus.Paused] = [SessionStatus.InProgress, SessionStatus.Collecting, SessionStatus.Finished, SessionStatus.Cancelled],
            [SessionStatus.Collecting] = [SessionStatus.Finished, SessionStatus.Cancelled],
            [SessionStatus.Finished] = [SessionStatus.Archived],
            [SessionStatus.Archived] = [],
            [SessionStatus.Cancelled] = [SessionStatus.Archived]
        };

    public static void EnsureTransition(SessionStatus current, SessionStatus next)
    {
        if (current == next) return;
        if (!Allowed.TryGetValue(current, out var targets) || !targets.Contains(next))
            throw new DomainRuleException(ErrorCodes.InvalidStateTransition, $"Không thể chuyển trạng thái phòng thi từ {current} sang {next}.");
    }
}

public static class SubmissionStateMachine
{
    private static readonly IReadOnlyDictionary<SubmissionStatus, HashSet<SubmissionStatus>> Allowed =
        new Dictionary<SubmissionStatus, HashSet<SubmissionStatus>>
        {
            [SubmissionStatus.NotStarted] = [SubmissionStatus.Preparing],
            [SubmissionStatus.Preparing] = [SubmissionStatus.Uploading, SubmissionStatus.Failed],
            [SubmissionStatus.Uploading] = [SubmissionStatus.Verifying, SubmissionStatus.Failed],
            [SubmissionStatus.Verifying] = [SubmissionStatus.Submitted, SubmissionStatus.LateSubmitted, SubmissionStatus.Failed],
            [SubmissionStatus.Failed] = [SubmissionStatus.Uploading],
            [SubmissionStatus.Submitted] = [SubmissionStatus.Rejected],
            [SubmissionStatus.LateSubmitted] = [SubmissionStatus.Rejected],
            [SubmissionStatus.Rejected] = []
        };

    public static void EnsureTransition(SubmissionStatus current, SubmissionStatus next)
    {
        if (current == next) return;
        if (!Allowed.TryGetValue(current, out var targets) || !targets.Contains(next))
            throw new DomainRuleException(ErrorCodes.InvalidStateTransition, $"Không thể chuyển trạng thái bài nộp từ {current} sang {next}.");
    }
}
