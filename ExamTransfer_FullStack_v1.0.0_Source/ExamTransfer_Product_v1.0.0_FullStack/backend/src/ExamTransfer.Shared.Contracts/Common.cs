using System.Text.Json.Serialization;

namespace ExamTransfer.Shared.Contracts;

public static class ContractInfo
{
    public const string SchemaVersion = "1.5.0";
    public const string ApiVersion = "v1";
    public const string HubPath = "/hubs/exam";
}

public static class StudentSubmissionPolicy
{
    public const long MaxBytes = 10L * 1024 * 1024;
    public const int MaxFileCount = 1;
    public static readonly IReadOnlySet<string> AllowedExtensions =
        new HashSet<string>([".zip", ".rar", ".7z"], StringComparer.OrdinalIgnoreCase);

    public static bool IsAllowedExtension(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && AllowedExtensions.Contains(Path.GetExtension(Path.GetFileName(fileName)));
}

public sealed record ApiError(
    string Code,
    string Message,
    IReadOnlyDictionary<string, string[]>? FieldErrors = null,
    object? Details = null);

public sealed record ApiResponse<T>(
    bool Success,
    T? Data,
    ApiError? Error,
    string TraceId,
    string SchemaVersion)
{
    public static ApiResponse<T> Ok(T data, string traceId) =>
        new(true, data, null, traceId, ContractInfo.SchemaVersion);

    public static ApiResponse<T> Fail(ApiError error, string traceId) =>
        new(false, default, error, traceId, ContractInfo.SchemaVersion);
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public sealed record FileRuleDto(
    IReadOnlyList<string> AllowedExtensions,
    long MaxFileSizeBytes,
    long MaxTotalSizeBytes,
    int MaxFileCount,
    bool AutoZip,
    bool RequireAtLeastOneFile);

public sealed record FileDescriptorDto(
    Guid Id,
    string Name,
    long SizeBytes,
    string Sha256,
    string MimeType,
    string? DownloadUrl = null);

public sealed record ChunkPlanDto(Guid FileId, int TotalChunks, IReadOnlyList<int> MissingChunks);


public static class CloudAccessModes
{
    /// <summary>
    /// Distributed desktop/local-server mode. Supabase requests use a
    /// publishable key plus the signed-in teacher/admin access token, so RLS
    /// remains the authorization boundary.
    /// </summary>
    public const string UserSession = "UserSession";

    /// <summary>
    /// Optional mode for a school-owned, administrator-controlled server. The
    /// secret key must be supplied through a trusted environment variable and
    /// is never returned to or stored by the Frontend.
    /// </summary>
    public const string TrustedServer = "TrustedServer";

    public static bool IsValid(string? value) =>
        string.Equals(value, UserSession, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, TrustedServer, StringComparison.OrdinalIgnoreCase);
}

public static class ErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
    public const string TokenExpired = "TOKEN_EXPIRED";
    public const string NotFound = "NOT_FOUND";
    public const string Conflict = "CONFLICT";
    public const string InvalidStateTransition = "INVALID_STATE_TRANSITION";
    public const string SessionAlreadyStarted = "SESSION_ALREADY_STARTED";
    public const string SubmissionFinalized = "SUBMISSION_FINALIZED";
    public const string DuplicateStudentCode = "DUPLICATE_STUDENT_CODE";
    public const string RoomCodeConflict = "ROOM_CODE_CONFLICT";
    public const string InvalidFileType = "INVALID_FILE_TYPE";
    public const string FileTooLarge = "FILE_TOO_LARGE";
    public const string ChunkMismatch = "CHUNK_MISMATCH";
    public const string HashMismatch = "HASH_MISMATCH";
    public const string TransferExpired = "TRANSFER_EXPIRED";
    public const string DeadlinePassed = "DEADLINE_PASSED";
    public const string StorageFull = "STORAGE_FULL";
    public const string StaleSequence = "STALE_SEQUENCE";
    public const string AgentUnavailable = "AGENT_UNAVAILABLE";
    public const string PolicyUnsupported = "POLICY_UNSUPPORTED";
    public const string PolicyApplyFailed = "POLICY_APPLY_FAILED";
    public const string CloudOffline = "CLOUD_OFFLINE";
    public const string SyncConflict = "SYNC_CONFLICT";
    public const string CloudUploadFailed = "CLOUD_UPLOAD_FAILED";
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string AccountInactive = "ACCOUNT_INACTIVE";
    public const string StudentIdentityConfirmationRequired = "STUDENT_IDENTITY_CONFIRMATION_REQUIRED";
    public const string StudentIdentityMismatch = "STUDENT_IDENTITY_MISMATCH";
    public const string AccountAlreadyActive = "ACCOUNT_ALREADY_ACTIVE";
    public const string LoginSessionExpired = "LOGIN_SESSION_EXPIRED";
    public const string DeviceMismatch = "DEVICE_MISMATCH";
    public const string AuthProviderUnavailable = "AUTH_PROVIDER_UNAVAILABLE";
    public const string SupabaseNotConfigured = "SUPABASE_NOT_CONFIGURED";
    public const string AuthResponseInvalid = "AUTH_RESPONSE_INVALID";
    public const string AuthAccessTokenMissing = "AUTH_ACCESS_TOKEN_MISSING";
    public const string ProfileAccessUnauthorized = "PROFILE_ACCESS_UNAUTHORIZED";
    public const string ProfileAccessForbidden = "PROFILE_ACCESS_FORBIDDEN";
    public const string ProfileLookupFailed = "PROFILE_LOOKUP_FAILED";
    public const string ProfileNotFound = "PROFILE_NOT_FOUND";
    public const string ProfileResponseInvalid = "PROFILE_RESPONSE_INVALID";
    public const string ProfileOrganizationMissing = "PROFILE_ORGANIZATION_MISSING";
    public const string ProfileOrganizationMismatch = "PROFILE_ORGANIZATION_MISMATCH";
    public const string ProfileRoleInvalid = "PROFILE_ROLE_INVALID";
    public const string AccountProvisioningConflict = "ACCOUNT_PROVISIONING_CONFLICT";
    public const string InvalidCurrentPassword = "INVALID_CURRENT_PASSWORD";
    public const string PasswordPolicyRejected = "PASSWORD_POLICY_REJECTED";
    public const string PasswordChangeFailed = "PASSWORD_CHANGE_FAILED";
    public const string PasswordChangeRequired = "PASSWORD_CHANGE_REQUIRED";
    public const string ParticipantTokenRequired = "PARTICIPANT_TOKEN_REQUIRED";
    public const string ParticipantAccountMismatch = "PARTICIPANT_ACCOUNT_MISMATCH";
    public const string SubmissionArchiveRequired = "SUBMISSION_ARCHIVE_REQUIRED";
    public const string SubmissionTooLarge = "SUBMISSION_TOO_LARGE";
    public const string SubmissionFileCountInvalid = "SUBMISSION_FILE_COUNT_INVALID";
    public const string LanAccessDenied = "LAN_ACCESS_DENIED";
    public const string EnrollmentClosed = "ENROLLMENT_CLOSED";
    public const string EnrollmentCodeInvalid = "ENROLLMENT_CODE_INVALID";
    public const string EnrollmentDuplicate = "ENROLLMENT_DUPLICATE";
    public const string DeviceCommandExpired = "DEVICE_COMMAND_EXPIRED";
}
