namespace ExamTransfer.Shared.Contracts;

public sealed record AccountLoginResultDto(
    bool Authenticated,
    bool RequiresStudentConfirmation,
    string? ChallengeToken,
    Guid? UserId,
    string? DisplayName,
    string? StudentCode,
    UserRole? Role,
    string? OrganizationId,
    string? AccessToken,
    DateTimeOffset? ExpiresAtUtc,
    string DeviceId);

public sealed record CurrentAccountDto(
    Guid UserId,
    string Username,
    string? Email,
    string DisplayName,
    string? StudentCode,
    UserRole Role,
    string? OrganizationId,
    Guid LoginSessionId,
    string DeviceId,
    DateTimeOffset ExpiresAtUtc,
    DateOnly? DateOfBirth = null,
    bool MustChangePassword = false);
