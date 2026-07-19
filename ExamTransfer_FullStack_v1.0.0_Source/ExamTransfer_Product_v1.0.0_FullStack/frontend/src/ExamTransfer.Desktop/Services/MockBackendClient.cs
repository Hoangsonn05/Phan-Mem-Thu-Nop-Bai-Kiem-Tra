using System.Text;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Services;

/// <summary>
/// Stateful in-memory implementation used for product smoke tests without a running server.
/// It follows the same contracts and state transitions as the REST backend.
/// </summary>
public sealed class MockBackendClient : IBackendClient
{
    private readonly List<ClassDetailDto> classes = new();
    private readonly List<ExamDetailDto> exams = new();
    private readonly List<SessionDetailDto> sessions = new();
    private readonly List<SubmissionSummaryDto> submissions = new();
    private readonly List<ExportJobDto> exports = new();
    private readonly List<BackupDto> backups = new();
    private readonly List<AuditLogDto> audits = new();
    private readonly Dictionary<Guid, ReceiptDto> receipts = new();
    private readonly Dictionary<Guid, GradeDto> grades = new();
    private readonly Dictionary<Guid, ControlPolicyDto> policies = new();
    private readonly List<DeviceControlStatusDto> deviceStatuses = new();
    private readonly List<ViolationDto> violations = new();
    private readonly Dictionary<Guid, PendingFileUpload> examUploads = new();
    private string? accountToken;
    private string? participantToken;
    private CurrentAccountDto? currentAccount;
    private CloudSessionDto cloudSession = new(
        false,
        null,
        null,
        null,
        null,
        null);

    private SettingsDto settings = new(
        5048,
        false,
        true,
        5050,
        @"C:\ProgramData\ExamTransfer",
        5L * 1024 * 1024 * 1024,
        4_194_304,
        8,
        5,
        20,
        false,
        24,
        30,
        "1",
        "https://example.supabase.co",
        "sb_publishable_local_development",
        Guid.NewGuid().ToString(),
        "Development",
        true,
        false,
        "Chưa đăng nhập",
        CloudAccessModes.UserSession,
        false,
        null);

    public MockBackendClient()
    {
        var classId = Guid.NewGuid();
        var studentOne = new StudentDto(Guid.NewGuid(), "SV001", "Nguyễn Minh Anh", "sv001@example.local", null);
        var studentTwo = new StudentDto(Guid.NewGuid(), "SV002", "Trần Quốc Bảo", "sv002@example.local", null);
        classes.Add(new ClassDetailDto(
            classId,
            "Công nghệ thông tin K17A",
            "CNTT17A",
            "2026-2027",
            "Lớp thực hành lập trình",
            ClassStatus.Active,
            new[] { studentOne, studentTwo },
            "1"));

        var examId = Guid.NewGuid();
        var examFile = new FileDescriptorDto(
            Guid.NewGuid(),
            "DeThi_Java.pdf",
            8_400_000,
            "9a97e57f3bc242a6baf2fa2abbadf5d8",
            "application/pdf",
            $"api/v1/exams/{examId}/files/content");
        exams.Add(new ExamDetailDto(
            examId,
            classId,
            "Kiểm tra Lập trình Java",
            "Java",
            "Bài kiểm tra thực hành 60 phút",
            60,
            ExamStatus.Published,
            1,
            new FileRuleDto(new[] { ".java", ".zip", ".pdf" }, 50_000_000, 200_000_000, 8, true, true),
            new[] { examFile },
            "1"));

        var sessionId = Guid.NewGuid();
        var participantOne = NewParticipant(sessionId, "SV001", "Nguyễn Minh Anh", ParticipantStatus.Approved, SubmissionStatus.Submitted);
        var participantTwo = NewParticipant(sessionId, "SV002", "Trần Quốc Bảo", ParticipantStatus.PendingApproval, SubmissionStatus.NotStarted);
        var participants = new[] { participantOne, participantTwo };
        sessions.Add(new SessionDetailDto(
            NewSessionSummary(sessionId, examId, "Kiểm tra Lập trình Java", "JAVA24", SessionStatus.Waiting, participants),
            participants,
            """{"autoApprove":false,"capacity":36}"""));

        var submissionId = Guid.NewGuid();
        var submissionFileId = Guid.NewGuid();
        var submissionFile = new SubmissionFileDto(
            submissionFileId,
            "SV001_BaiLam.zip",
            1_240_000,
            "4f9c2a23c6d64145bce51ecf839d33cb",
            "application/zip",
            1,
            new[] { 0 },
            TransferStatus.Completed,
            $"api/v1/submissions/{submissionId}/files/{submissionFileId}/content");
        var submission = new SubmissionSummaryDto(
            submissionId,
            sessionId,
            participantOne.Id,
            participantOne.StudentCode,
            participantOne.DisplayName,
            1,
            SubmissionStatus.Submitted,
            DateTimeOffset.UtcNow.AddMinutes(-15),
            DateTimeOffset.UtcNow.AddMinutes(-14),
            DateTimeOffset.UtcNow.AddMinutes(45),
            false,
            "RC-100001",
            true,
            new[] { submissionFile });
        submissions.Add(submission);
        receipts[submissionId] = new ReceiptDto(
            submissionId,
            "RC-100001",
            "mock-signature-100001",
            submission.ServerReceivedAtUtc!.Value,
            false,
            new[] { new FileDescriptorDto(submissionFileId, submissionFile.Name, submissionFile.SizeBytes, submissionFile.Sha256, submissionFile.MimeType) });
        grades[submissionId] = NewGrade(submissionId, GradingStatus.NotGraded);

        policies[sessionId] = new ControlPolicyDto(
            sessionId,
            1,
            true,
            "WarnOnFocusLost",
            "BlockPaste",
            new[] { "ExamTransfer.Desktop.exe", "WINWORD.EXE" },
            new[] { "chrome.exe", "msedge.exe" },
            "LocalOnly",
            true,
            120,
            "1");
        deviceStatuses.Add(new DeviceControlStatusDto(
            participantOne.Id,
            1,
            new ControlCapabilitiesDto(true, true, true, true, false),
            PolicyApplyStatus.Applied,
            null,
            DateTimeOffset.UtcNow));
        violations.Add(new ViolationDto(
            Guid.NewGuid(),
            sessionId,
            participantOne.Id,
            "FocusLost",
            ViolationSeverity.Warning,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            """{"durationSeconds":4}""",
            null,
            null));

        backups.Add(new BackupDto(
            Guid.NewGuid(),
            "ExamTransfer-backup-initial.etbak",
            12_400_000,
            "7c6c760067e943e39f38de42e57dfab0",
            ContractInfo.SchemaVersion,
            false,
            BackupStatus.Ready,
            DateTimeOffset.UtcNow.AddDays(-1)));

        Audit("SeedReady", "System", null);
    }

    public void SetBearerToken(string? token) => SetAccountToken(token);
    public void SetAccountToken(string? token) => accountToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    public void SetParticipantToken(string? token) => participantToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim();

    public Task<ApiResponse<SystemStatusDto>?> GetSystemStatusAsync(CancellationToken ct = default) =>
        Result(new SystemStatusDto(
            true,
            "1.0.0",
            DateTimeOffset.UtcNow,
            "Ready",
            "Ready",
            80L * 1024 * 1024 * 1024,
            "Running",
            settings.CloudEnabled ? "Pending" : "LocalOnly",
            Array.Empty<string>()));

    public Task<ApiResponse<DashboardSummaryDto>?> GetDashboardAsync(CancellationToken ct = default)
    {
        var dto = new DashboardSummaryDto(
            classes.Count(x => x.Status == ClassStatus.Active),
            exams.Count(x => x.Status != ExamStatus.Archived),
            sessions.Count(x => x.Summary.Status is SessionStatus.Waiting or SessionStatus.InProgress or SessionStatus.Paused),
            submissions.Count(x => !grades.TryGetValue(x.Id, out var grade) || grade.Status == GradingStatus.NotGraded),
            2_400_000_000,
            sessions.Select(x => Recalculate(x).Summary).OrderByDescending(x => x.ServerNowUtc).Take(5).ToArray(),
            sessions.Any(x => x.Participants.Any(p => p.ConnectionState != ConnectionState.Online))
                ? new[] { "Có thiết bị đang mất kết nối trong phiên gần nhất." }
                : Array.Empty<string>());
        return Result(dto);
    }

    public Task<ApiResponse<PagedResult<ClassSummaryDto>>?> GetClassesAsync(CancellationToken ct = default) =>
        Result(Page(classes.Select(ToSummary).ToArray()));

    public Task<ApiResponse<PagedResult<ExamSummaryDto>>?> GetExamsAsync(CancellationToken ct = default) =>
        Result(Page(exams.Select(ToSummary).ToArray()));

    public Task<ApiResponse<PagedResult<SessionSummaryDto>>?> GetSessionsAsync(CancellationToken ct = default) =>
        Result(Page(sessions.Select(Recalculate).Select(x => x.Summary).ToArray()));

    public Task<ApiResponse<SessionDetailDto>?> GetSessionAsync(Guid id, CancellationToken ct = default) =>
        Result(Recalculate(RequireSession(id)));

    public Task<ApiResponse<PagedResult<SubmissionSummaryDto>>?> GetSubmissionsAsync(Guid sessionId, CancellationToken ct = default) =>
        Result(Page(submissions.Where(x => x.SessionId == sessionId).OrderByDescending(x => x.AttemptNumber).ToArray()));

    public Task<ApiResponse<CloudSyncStatusDto>?> GetCloudStatusAsync(CancellationToken ct = default) =>
        Result(new CloudSyncStatusDto(
            settings.CloudEnabled,
            settings.CloudEnabled ? SyncStatus.Pending : SyncStatus.LocalOnly,
            settings.CloudEnabled ? 3 : 0,
            null,
            null,
            settings.CloudEnabled,
            settings.OrganizationId,
            settings.CloudUseResumableUploads ? "TUS resumable" : "Standard upload",
            settings.CloudAccessMode,
            cloudSession.Authenticated,
            settings.CloudAccessMode == CloudAccessModes.TrustedServer
                ? settings.CloudSecretConfigured
                : cloudSession.Authenticated));

    public Task<ApiResponse<SettingsDto>?> GetSettingsAsync(CancellationToken ct = default) => Result(settings);

    public Task<ApiResponse<T>?> GetAsync<T>(string path, CancellationToken ct = default)
    {
        var route = Normalize(path);
        object? value;

        if (route == "api/v1/auth/me")
        {
            value = currentAccount ?? throw new InvalidOperationException("Chưa đăng nhập.");
        }
        else if (route == "api/v1/settings")
        {
            value = settings;
        }
        else if (route == "api/v1/cloud/sync/status")
        {
            value = new CloudSyncStatusDto(
                settings.CloudEnabled,
                settings.CloudEnabled ? SyncStatus.Pending : SyncStatus.LocalOnly,
                settings.CloudEnabled ? 3 : 0,
                null,
                null,
                settings.CloudEnabled,
                settings.OrganizationId,
                settings.CloudUseResumableUploads ? "TUS resumable" : "Standard upload",
                settings.CloudAccessMode,
                cloudSession.Authenticated,
                settings.CloudAccessMode == CloudAccessModes.TrustedServer
                    ? settings.CloudSecretConfigured
                    : cloudSession.Authenticated);
        }
        else if (route == "api/v1/cloud/preflight")
        {
            var baseConfigured = settings.CloudEnabled
                && !string.IsNullOrWhiteSpace(settings.SupabaseUrl)
                && !string.IsNullOrWhiteSpace(settings.SupabasePublishableKey)
                && Guid.TryParse(settings.OrganizationId, out _);
            var canSynchronize = baseConfigured
                && (settings.CloudAccessMode == CloudAccessModes.TrustedServer
                    ? settings.CloudSecretConfigured
                    : cloudSession.Authenticated);
            value = new CloudPreflightDto(
                settings.CloudEnabled,
                baseConfigured,
                canSynchronize,
                settings.CloudSecretConfigured,
                settings.CloudAccessMode == CloudAccessModes.TrustedServer
                    ? "Trusted secret key"
                    : "Publishable key + user access token",
                settings.OrganizationId,
                settings.CloudUseResumableUploads
                    ? "TUS resumable for files over 6 MiB"
                    : "Standard upload",
                baseConfigured
                    ? Array.Empty<string>()
                    : new[] { "Cấu hình Supabase chưa hoàn chỉnh." },
                !canSynchronize && baseConfigured
                    ? new[] { "Cần đăng nhập Supabase hoặc cấu hình TrustedServer secret." }
                    : Array.Empty<string>(),
                settings.CloudAccessMode,
                cloudSession.Authenticated,
                cloudSession.Email,
                canSynchronize);
        }
        else if (route == "api/v1/cloud/auth/session")
        {
            value = cloudSession;
        }
        else if (route == "api/v1/system/diagnostics")
        {
            value = new Dictionary<string, object>
            {
                ["serverPort"] = settings.ServerPort,
                ["discoveryPort"] = settings.DiscoveryPort,
                ["storageRoot"] = settings.StorageRootPath,
                ["cloudEnabled"] = settings.CloudEnabled,
                ["mockSession"] = true
            };
        }
        else if (route.StartsWith("api/v1/classes/", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var classId))
        {
            value = RequireClass(classId);
        }
        else if (route.StartsWith("api/v1/exams/", StringComparison.OrdinalIgnoreCase) && route.EndsWith("/manifest", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var manifestExamId))
        {
            var exam = RequireExam(manifestExamId);
            value = new ExamManifestDto(exam.Id, exam.Version, DateTimeOffset.UtcNow, exam.Files);
        }
        else if (route.StartsWith("api/v1/exams/", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var examId))
        {
            value = RequireExam(examId);
        }
        else if (route == "api/v1/grading/queue")
        {
            value = Page(submissions.OrderByDescending(x => x.ServerReceivedAtUtc).ToArray());
        }
        else if (route.StartsWith("api/v1/grading/submissions/", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 4, out var gradingSubmissionId))
        {
            value = grades.TryGetValue(gradingSubmissionId, out var grade) ? grade : NewGrade(gradingSubmissionId, GradingStatus.NotGraded);
        }
        else if (route.StartsWith("api/v1/student/submissions/", StringComparison.OrdinalIgnoreCase) && route.EndsWith("/grade", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 4, out var studentGradeId))
        {
            value = grades.TryGetValue(studentGradeId, out var grade) && grade.Status == GradingStatus.Returned ? grade : NewGrade(studentGradeId, GradingStatus.NotGraded);
        }
        else if (route.StartsWith("api/v1/sessions/", StringComparison.OrdinalIgnoreCase) && route.EndsWith("/control-policy", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var policySessionId))
        {
            value = policies.TryGetValue(policySessionId, out var policy) ? policy : null;
        }
        else if (route.StartsWith("api/v1/sessions/", StringComparison.OrdinalIgnoreCase) && route.EndsWith("/devices/control-status", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var deviceSessionId))
        {
            var participantIds = RequireSession(deviceSessionId).Participants.Select(x => x.Id).ToHashSet();
            value = (IReadOnlyList<DeviceControlStatusDto>)deviceStatuses.Where(x => participantIds.Contains(x.ParticipantId)).ToArray();
        }
        else if (route.StartsWith("api/v1/sessions/", StringComparison.OrdinalIgnoreCase) && route.EndsWith("/violations", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var violationSessionId))
        {
            value = Page(violations.Where(x => x.SessionId == violationSessionId).OrderByDescending(x => x.OccurredAtUtc).ToArray());
        }
        else if (route.StartsWith("api/v1/sessions/", StringComparison.OrdinalIgnoreCase) && route.Contains("/participants/", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var participantSessionId) && TryGuidAt(route, 5, out var participantId))
        {
            value = RequireSession(participantSessionId).Participants.FirstOrDefault(x => x.Id == participantId)
                ?? throw new InvalidOperationException("Không tìm thấy người tham gia trong phòng.");
        }
        else if (route.StartsWith("api/v1/sessions/", StringComparison.OrdinalIgnoreCase) && route.EndsWith("/submissions", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var sessionSubmissionId))
        {
            value = Page(submissions.Where(x => x.SessionId == sessionSubmissionId).OrderByDescending(x => x.ServerReceivedAtUtc).ToArray());
        }
        else if (route.StartsWith("api/v1/sessions/", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var sessionId))
        {
            value = Recalculate(RequireSession(sessionId));
        }
        else if (route.StartsWith("api/v1/submissions/", StringComparison.OrdinalIgnoreCase) && route.EndsWith("/receipt", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var receiptSubmissionId))
        {
            value = receipts.TryGetValue(receiptSubmissionId, out var receipt)
                ? receipt
                : throw new InvalidOperationException("Bài nộp chưa có biên nhận.");
        }
        else if (route.StartsWith("api/v1/submissions/", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var submissionId))
        {
            value = submissions.FirstOrDefault(x => x.Id == submissionId)
                ?? throw new InvalidOperationException("Không tìm thấy bài nộp.");
        }
        else if (route.StartsWith("api/v1/exports/", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var exportId))
        {
            value = exports.FirstOrDefault(x => x.Id == exportId)
                ?? throw new InvalidOperationException("Không tìm thấy tác vụ xuất dữ liệu.");
        }
        else if (route == "api/v1/backups")
        {
            value = (IReadOnlyList<BackupDto>)backups.OrderByDescending(x => x.CreatedAtUtc).ToArray();
        }
        else if (route == "api/v1/audit-logs")
        {
            value = Page(audits.OrderByDescending(x => x.CreatedAtUtc).ToArray());
        }
        else if (route == "api/v1/history/sessions")
        {
            value = Page(sessions.Select(Recalculate).Select(x => x.Summary).OrderByDescending(x => x.ServerNowUtc).ToArray());
        }
        else if (route.StartsWith("api/v1/history/sessions/", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var historyId))
        {
            value = Recalculate(RequireSession(historyId));
        }
        else if (route == "api/v1/classes")
        {
            value = Page(classes.Select(ToSummary).ToArray());
        }
        else if (route == "api/v1/exams")
        {
            value = Page(exams.Select(ToSummary).ToArray());
        }
        else if (route == "api/v1/sessions")
        {
            value = Page(sessions.Select(Recalculate).Select(x => x.Summary).ToArray());
        }
        else
        {
            value = default(T);
        }

        return Result(Cast<T>(value));
    }

    public Task<ApiResponse<TResponse>?> PostAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct = default)
    {
        var value = HandlePost(Normalize(path), request);
        return Result(Cast<TResponse>(value));
    }

    public Task<ApiResponse<TResponse>?> PutAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken ct = default)
    {
        var route = Normalize(path);
        object? value;

        if (route == "api/v1/settings" && request is UpdateSettingsRequest updateSettings)
        {
            settings = new SettingsDto(
                updateSettings.ServerPort,
                updateSettings.UseHttps,
                updateSettings.DiscoveryEnabled,
                updateSettings.DiscoveryPort,
                updateSettings.StorageRootPath,
                updateSettings.MinFreeBytes,
                updateSettings.ChunkSizeBytes,
                updateSettings.MaxConcurrentUploads,
                updateSettings.HeartbeatSeconds,
                updateSettings.DisconnectAfterSeconds,
                updateSettings.CloudEnabled,
                updateSettings.TemporaryHours,
                updateSettings.LogsDays,
                NextVersion(settings.RowVersion),
                updateSettings.SupabaseUrl,
                updateSettings.SupabasePublishableKey,
                updateSettings.OrganizationId,
                updateSettings.CloudEnvironment,
                updateSettings.CloudUseResumableUploads,
                settings.CloudSecretConfigured,
                updateSettings.CloudEnabled
                    ? "Chờ kiểm tra kết nối"
                    : "Đã tắt",
                updateSettings.CloudAccessMode,
                cloudSession.Authenticated,
                cloudSession.Email);
            Audit("UpdateSettings", "Settings", null);
            value = settings;
        }
        else if (route.StartsWith("api/v1/grading/submissions/", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 4, out var gradeSubmissionId) && request is SaveGradeRequest saveGrade)
        {
            var grade = new GradeDto(
                gradeSubmissionId,
                GradingStatus.Graded,
                saveGrade.Score,
                saveGrade.MaxScore,
                saveGrade.RubricScores,
                saveGrade.GeneralComment,
                grades.TryGetValue(gradeSubmissionId, out var oldGrade) ? oldGrade.Attachments : Array.Empty<FileDescriptorDto>(),
                oldGrade?.ReturnedAtUtc,
                NextVersion(oldGrade?.RowVersion ?? "0"));
            grades[gradeSubmissionId] = grade;
            Audit("SaveGrade", "Submission", gradeSubmissionId.ToString());
            value = grade;
        }
        else if (route.StartsWith("api/v1/sessions/", StringComparison.OrdinalIgnoreCase) && route.EndsWith("/control-policy", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var controlSessionId) && request is SaveControlPolicyRequest savePolicy)
        {
            var currentVersion = policies.TryGetValue(controlSessionId, out var oldPolicy) ? oldPolicy.Version + 1 : 1;
            var policy = new ControlPolicyDto(
                controlSessionId,
                currentVersion,
                savePolicy.Fullscreen,
                savePolicy.FocusRule,
                savePolicy.ClipboardRule,
                savePolicy.AllowedProcesses,
                savePolicy.BlockedProcesses,
                savePolicy.NetworkRule,
                savePolicy.EmergencyExit,
                savePolicy.TtlMinutes,
                NextVersion(oldPolicy?.RowVersion ?? "0"));
            policies[controlSessionId] = policy;
            Audit("SaveControlPolicy", "Session", controlSessionId.ToString());
            value = policy;
        }
        else if (route.StartsWith("api/v1/classes/", StringComparison.OrdinalIgnoreCase) && route.Contains("/students/", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var studentClassId) && TryGuidAt(route, 5, out var studentId) && request is UpdateStudentRequest updateStudent)
        {
            var classIndex = classes.FindIndex(x => x.Id == studentClassId);
            if (classIndex < 0) throw new InvalidOperationException("Không tìm thấy lớp học.");
            var current = classes[classIndex];
            var students = current.Students.ToArray();
            var studentIndex = Array.FindIndex(students, x => x.Id == studentId);
            if (studentIndex < 0) throw new InvalidOperationException("Không tìm thấy học sinh.");
            students[studentIndex] = students[studentIndex] with
            {
                StudentCode = updateStudent.StudentCode,
                DisplayName = updateStudent.DisplayName,
                Email = updateStudent.Email,
                MetadataJson = updateStudent.MetadataJson
            };
            classes[classIndex] = current with { Students = students, RowVersion = NextVersion(current.RowVersion) };
            value = students[studentIndex];
        }
        else if (route.StartsWith("api/v1/classes/", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var classId) && request is UpdateClassRequest updateClass)
        {
            var index = classes.FindIndex(x => x.Id == classId);
            if (index < 0) throw new InvalidOperationException("Không tìm thấy lớp học.");
            var current = classes[index];
            classes[index] = current with
            {
                Name = updateClass.Name,
                Code = updateClass.Code,
                SchoolYear = updateClass.SchoolYear,
                Description = updateClass.Description,
                RowVersion = NextVersion(current.RowVersion)
            };
            value = classes[index];
        }
        else if (route.StartsWith("api/v1/exams/", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var examId) && request is UpdateExamRequest updateExam)
        {
            var index = exams.FindIndex(x => x.Id == examId);
            if (index < 0) throw new InvalidOperationException("Không tìm thấy bài kiểm tra.");
            var current = exams[index];
            exams[index] = current with
            {
                ClassId = updateExam.ClassId,
                Title = updateExam.Title,
                Subject = updateExam.Subject,
                Description = updateExam.Description,
                DurationMinutes = updateExam.DurationMinutes,
                FileRule = updateExam.FileRule,
                RowVersion = NextVersion(current.RowVersion)
            };
            value = exams[index];
        }
        else
        {
            value = default(TResponse);
        }

        return Result(Cast<TResponse>(value));
    }

    public Task<ApiResponse<TResponse>?> DeleteAsync<TResponse>(string path, CancellationToken ct = default)
    {
        var route = Normalize(path);
        if (route.StartsWith("api/v1/classes/", StringComparison.OrdinalIgnoreCase) && route.Contains("/students/", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var studentClassId) && TryGuidAt(route, 5, out var studentId))
        {
            var classIndex = classes.FindIndex(x => x.Id == studentClassId);
            if (classIndex >= 0)
            {
                var current = classes[classIndex];
                classes[classIndex] = current with { Students = current.Students.Where(x => x.Id != studentId).ToArray(), RowVersion = NextVersion(current.RowVersion) };
                Audit("RemoveStudent", "Student", studentId.ToString());
            }
        }
        else if (route.StartsWith("api/v1/classes/", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var classId))
        {
            var index = classes.FindIndex(x => x.Id == classId);
            if (index >= 0)
            {
                classes[index] = classes[index] with { Status = ClassStatus.Archived, RowVersion = NextVersion(classes[index].RowVersion) };
                Audit("ArchiveClass", "Class", classId.ToString());
            }
        }
        else if (route.StartsWith("api/v1/exams/", StringComparison.OrdinalIgnoreCase) && route.Contains("/files/", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var examId) && TryGuidAt(route, 5, out var fileId))
        {
            var index = exams.FindIndex(x => x.Id == examId);
            if (index >= 0)
            {
                exams[index] = exams[index] with
                {
                    Files = exams[index].Files.Where(x => x.Id != fileId).ToArray(),
                    RowVersion = NextVersion(exams[index].RowVersion)
                };
            }
        }
        return Result(Cast<TResponse>(default(TResponse)));
    }

    public Task<ApiResponse<object>?> UploadChunkAsync(string path, Stream content, long contentLength, string? sha256 = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Result<object>(new { uploaded = contentLength, sha256, participantTokenPresent = !string.IsNullOrWhiteSpace(participantToken) });
    }

    public async Task DownloadFileAsync(string path, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? AppContext.BaseDirectory);
        var route = Normalize(path);
        var payload = route.Contains("audit", StringComparison.OrdinalIgnoreCase)
            ? "time,actor,action,entityType,entityId,traceId\n"
            : route.Contains(".zip", StringComparison.OrdinalIgnoreCase) || destinationPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? "ExamTransfer mock archive content"
                : "ExamTransfer mock file content";
        await WriteMockFileAsync(destinationPath, payload, progress, ct);
    }

    public async Task PostDownloadFileAsync<TRequest>(string path, TRequest request, string destinationPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var csv = new StringBuilder("time,actor,action,entityType,entityId,traceId\n");
        foreach (var row in audits.OrderBy(x => x.CreatedAtUtc))
        {
            csv.Append(row.CreatedAtUtc.ToString("O")).Append(',')
                .Append(row.ActorId).Append(',')
                .Append(row.Action).Append(',')
                .Append(row.EntityType).Append(',')
                .Append(row.EntityId).Append(',')
                .Append(row.TraceId).AppendLine();
        }
        await WriteMockFileAsync(destinationPath, csv.ToString(), progress, ct);
    }

    private object HandlePost<TRequest>(string route, TRequest request)
    {
        if (route == "api/v1/auth/login" && request is AccountLoginRequest accountLogin)
        {
            if (string.IsNullOrWhiteSpace(accountLogin.Account) || string.IsNullOrWhiteSpace(accountLogin.Password))
                throw new InvalidOperationException("Tài khoản và mật khẩu là bắt buộc.");

            var isStudent = accountLogin.Account.Contains("student", StringComparison.OrdinalIgnoreCase)
                || accountLogin.Account.Contains("sv", StringComparison.OrdinalIgnoreCase);
            if (isStudent)
            {
                return new AccountLoginResultDto(
                    false,
                    true,
                    "mock-challenge-token",
                    Guid.Parse("10000000-0000-0000-0000-000000000002"),
                    "Nguyễn Minh Anh",
                    "SV001",
                    UserRole.Student,
                    settings.OrganizationId,
                    null,
                    DateTimeOffset.UtcNow.AddMinutes(5),
                    accountLogin.DeviceId);
            }

            currentAccount = new CurrentAccountDto(
                Guid.Parse("10000000-0000-0000-0000-000000000001"),
                accountLogin.Account.Trim(),
                accountLogin.Account.Contains('@') ? accountLogin.Account.Trim() : null,
                "Giáo viên ExamTransfer",
                null,
                UserRole.Teacher,
                settings.OrganizationId,
                Guid.NewGuid(),
                accountLogin.DeviceId,
                DateTimeOffset.UtcNow.AddHours(8));
            accountToken = "mock-account-token-" + currentAccount.LoginSessionId.ToString("N");
            return new AccountLoginResultDto(
                true,
                false,
                null,
                currentAccount.UserId,
                currentAccount.DisplayName,
                currentAccount.StudentCode,
                currentAccount.Role,
                currentAccount.OrganizationId,
                accountToken,
                currentAccount.ExpiresAtUtc,
                currentAccount.DeviceId);
        }

        if (route == "api/v1/auth/student/confirm" && request is StudentIdentityConfirmRequest confirm)
        {
            if (!string.Equals(confirm.ChallengeToken, "mock-challenge-token", StringComparison.Ordinal)
                || !string.Equals(confirm.StudentCode, "SV001", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Thông tin xác nhận sinh viên không đúng.");
            }

            currentAccount = new CurrentAccountDto(
                Guid.Parse("10000000-0000-0000-0000-000000000002"),
                "student.sv001",
                "sv001@example.local",
                "Nguyễn Minh Anh",
                "SV001",
                UserRole.Student,
                settings.OrganizationId,
                Guid.NewGuid(),
                confirm.DeviceId,
                DateTimeOffset.UtcNow.AddHours(8));
            accountToken = "mock-account-token-" + currentAccount.LoginSessionId.ToString("N");
            return new AccountLoginResultDto(
                true,
                false,
                null,
                currentAccount.UserId,
                currentAccount.DisplayName,
                currentAccount.StudentCode,
                currentAccount.Role,
                currentAccount.OrganizationId,
                accountToken,
                currentAccount.ExpiresAtUtc,
                currentAccount.DeviceId);
        }

        if (route == "api/v1/auth/heartbeat")
        {
            if (currentAccount is null)
                throw new InvalidOperationException("Chưa đăng nhập.");
            return new AccountHeartbeatResponse(true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(120), 30);
        }

        if (route == "api/v1/auth/logout")
        {
            currentAccount = null;
            accountToken = null;
            participantToken = null;
            return new { loggedOut = true };
        }

        if (route == "api/v1/classes" && request is CreateClassRequest createClass)
        {
            var item = new ClassDetailDto(
                Guid.NewGuid(),
                createClass.Name,
                createClass.Code,
                createClass.SchoolYear,
                createClass.Description,
                ClassStatus.Active,
                Array.Empty<StudentDto>(),
                "1");
            classes.Add(item);
            Audit("CreateClass", "Class", item.Id.ToString());
            return item;
        }

        if (route.StartsWith("api/v1/classes/", StringComparison.OrdinalIgnoreCase) && route.EndsWith("/students", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var studentClassId) && request is CreateStudentRequest createStudent)
        {
            var index = classes.FindIndex(x => x.Id == studentClassId);
            if (index < 0) throw new InvalidOperationException("Không tìm thấy lớp học.");
            var student = new StudentDto(Guid.NewGuid(), createStudent.StudentCode, createStudent.DisplayName, createStudent.Email, createStudent.MetadataJson);
            var current = classes[index];
            classes[index] = current with
            {
                Students = current.Students.Concat(new[] { student }).ToArray(),
                RowVersion = NextVersion(current.RowVersion)
            };
            Audit("CreateStudent", "Student", student.Id.ToString());
            return student;
        }

        if (route.EndsWith("/imports/preview", StringComparison.OrdinalIgnoreCase))
        {
            return new ImportPreviewDto(
                "preview-" + Guid.NewGuid().ToString("N"),
                3,
                2,
                1,
                new[]
                {
                    new StudentDto(Guid.NewGuid(), "SV010", "Lê Hoàng Nam", null, null),
                    new StudentDto(Guid.NewGuid(), "SV011", "Phạm Thu Hà", null, null)
                },
                new[] { new ImportRowErrorDto(4, "StudentCode", ErrorCodes.DuplicateStudentCode, "Mã học sinh đã tồn tại trong lớp.") });
        }

        if (route.EndsWith("/imports/commit", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var importClassId))
        {
            var index = classes.FindIndex(x => x.Id == importClassId);
            if (index < 0) throw new InvalidOperationException("Không tìm thấy lớp học.");
            var current = classes[index];
            var imported = new[]
            {
                new StudentDto(Guid.NewGuid(), "SV010", "Lê Hoàng Nam", null, null),
                new StudentDto(Guid.NewGuid(), "SV011", "Phạm Thu Hà", null, null)
            };
            classes[index] = current with { Students = current.Students.Concat(imported).ToArray(), RowVersion = NextVersion(current.RowVersion) };
            Audit("ImportStudents", "Class", importClassId.ToString());
            return new ImportCommitResultDto(2, 1, Array.Empty<ImportRowErrorDto>());
        }

        if (route == "api/v1/exams" && request is CreateExamRequest createExam)
        {
            var exam = new ExamDetailDto(
                Guid.NewGuid(),
                createExam.ClassId,
                createExam.Title,
                createExam.Subject,
                createExam.Description,
                createExam.DurationMinutes,
                ExamStatus.Draft,
                1,
                createExam.FileRule,
                Array.Empty<FileDescriptorDto>(),
                "1");
            exams.Add(exam);
            Audit("CreateExam", "Exam", exam.Id.ToString());
            return exam;
        }

        if (route.EndsWith("/publish", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var publishId))
        {
            var index = exams.FindIndex(x => x.Id == publishId);
            if (index < 0) throw new InvalidOperationException("Không tìm thấy bài kiểm tra.");
            exams[index] = exams[index] with { Status = ExamStatus.Published, RowVersion = NextVersion(exams[index].RowVersion) };
            Audit("PublishExam", "Exam", publishId.ToString());
            return exams[index];
        }

        if (route.EndsWith("/clone", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var cloneId))
        {
            var original = RequireExam(cloneId);
            var clone = original with
            {
                Id = Guid.NewGuid(),
                Title = original.Title + " - Bản sao",
                Status = ExamStatus.Draft,
                Version = 1,
                RowVersion = "1"
            };
            exams.Add(clone);
            Audit("CloneExam", "Exam", clone.Id.ToString());
            return clone;
        }

        if (route.EndsWith("/archive", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var archiveExamId))
        {
            var index = exams.FindIndex(x => x.Id == archiveExamId);
            if (index >= 0) exams[index] = exams[index] with { Status = ExamStatus.Archived, RowVersion = NextVersion(exams[index].RowVersion) };
            Audit("ArchiveExam", "Exam", archiveExamId.ToString());
            return new { archived = true };
        }

        if (route.EndsWith("/files/init", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var uploadExamId) && request is InitFileUploadRequest initFile)
        {
            var fileId = Guid.NewGuid();
            var chunkSize = initFile.ChunkSizeBytes ?? settings.ChunkSizeBytes;
            var totalChunks = Math.Max(1, (int)Math.Ceiling(initFile.SizeBytes / (double)chunkSize));
            examUploads[fileId] = new PendingFileUpload(uploadExamId, initFile.FileName, initFile.SizeBytes, initFile.Sha256, initFile.MimeType);
            return new InitFileUploadResponse(fileId, chunkSize, totalChunks, Enumerable.Range(0, totalChunks).ToArray());
        }

        if (route.EndsWith("/finalize", StringComparison.OrdinalIgnoreCase) && route.Contains("/files/", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var finalizeExamId) && TryGuidAt(route, 5, out var finalizeFileId))
        {
            if (!examUploads.TryGetValue(finalizeFileId, out var pending)) throw new InvalidOperationException("Phiên tải file không tồn tại.");
            var descriptor = new FileDescriptorDto(finalizeFileId, pending.Name, pending.SizeBytes, pending.Sha256, pending.MimeType, $"api/v1/exams/{finalizeExamId}/files/{finalizeFileId}/content");
            var examIndex = exams.FindIndex(x => x.Id == finalizeExamId);
            if (examIndex < 0) throw new InvalidOperationException("Không tìm thấy bài kiểm tra.");
            exams[examIndex] = exams[examIndex] with
            {
                Files = exams[examIndex].Files.Concat(new[] { descriptor }).ToArray(),
                Version = exams[examIndex].Status == ExamStatus.Published ? exams[examIndex].Version + 1 : exams[examIndex].Version,
                RowVersion = NextVersion(exams[examIndex].RowVersion)
            };
            examUploads.Remove(finalizeFileId);
            Audit("FinalizeExamFile", "ExamFile", finalizeFileId.ToString());
            return descriptor;
        }

        if (route == "api/v1/sessions" && request is CreateSessionRequest createSession)
        {
            var id = Guid.NewGuid();
            var room = string.IsNullOrWhiteSpace(createSession.CustomRoomCode)
                ? "LAN" + Random.Shared.Next(100, 999)
                : createSession.CustomRoomCode.Trim();
            var exam = RequireExam(createSession.ExamId);
            var participants = Array.Empty<ParticipantDto>();
            var summary = NewSessionSummary(id, createSession.ExamId, exam.Title, room, SessionStatus.Draft, participants);
            var detail = new SessionDetailDto(summary, participants, createSession.SettingsJson);
            sessions.Add(detail);
            Audit("CreateSession", "Session", id.ToString());
            return detail;
        }

        if (route == "api/v1/sessions/join" && request is JoinSessionRequest joinRequest)
        {
            var sessionIndex = sessions.FindIndex(x => string.Equals(x.Summary.RoomCode, joinRequest.RoomCode, StringComparison.OrdinalIgnoreCase));
            if (sessionIndex < 0) throw new InvalidOperationException("Mã phòng không tồn tại.");
            var current = sessions[sessionIndex];
            if (current.Summary.Status is not (SessionStatus.Waiting or SessionStatus.Draft)) throw new InvalidOperationException("Phòng thi không nhận thêm học sinh.");
            var autoApprove = current.SettingsJson.Contains("\"autoApprove\":true", StringComparison.OrdinalIgnoreCase);
            var participant = NewParticipant(
                current.Summary.Id,
                joinRequest.StudentCode,
                joinRequest.DisplayName,
                autoApprove ? ParticipantStatus.Approved : ParticipantStatus.PendingApproval,
                SubmissionStatus.NotStarted) with
            {
                DeviceId = joinRequest.DeviceId,
                MachineName = joinRequest.MachineName,
                AppVersion = joinRequest.AppVersion,
                ConnectionState = ConnectionState.Online
            };
            var participants = current.Participants.Concat(new[] { participant }).ToArray();
            sessions[sessionIndex] = current with { Participants = participants };
            sessions[sessionIndex] = Recalculate(sessions[sessionIndex]);
            Audit("ParticipantJoin", "Participant", participant.Id.ToString());
            return new JoinSessionResponse(
                current.Summary.Id,
                participant.Id,
                participant.Status,
                "mock-student-token-" + participant.Id.ToString("N"),
                DateTimeOffset.UtcNow.AddHours(2),
                participant);
        }

        if (route.StartsWith("api/v1/sessions/", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var transitionSessionId))
        {
            if (route.EndsWith("/open", StringComparison.OrdinalIgnoreCase)) return Transition(transitionSessionId, SessionStatus.Waiting);
            if (route.EndsWith("/distribute", StringComparison.OrdinalIgnoreCase)) return Transition(transitionSessionId, SessionStatus.Distributing);
            if (route.EndsWith("/start", StringComparison.OrdinalIgnoreCase)) return Transition(transitionSessionId, SessionStatus.InProgress);
            if (route.EndsWith("/pause", StringComparison.OrdinalIgnoreCase)) return Transition(transitionSessionId, SessionStatus.Paused);
            if (route.EndsWith("/resume", StringComparison.OrdinalIgnoreCase)) return Transition(transitionSessionId, SessionStatus.InProgress);
            if (route.EndsWith("/collect", StringComparison.OrdinalIgnoreCase)) return Transition(transitionSessionId, SessionStatus.Collecting);
            if (route.EndsWith("/end", StringComparison.OrdinalIgnoreCase)) return Transition(transitionSessionId, SessionStatus.Finished);
            if (route.EndsWith("/cancel", StringComparison.OrdinalIgnoreCase)) return Transition(transitionSessionId, SessionStatus.Cancelled);

            if (route.EndsWith("/participants/bulk-approve", StringComparison.OrdinalIgnoreCase) && request is BulkApproveRequest bulkApprove)
            {
                var detail = RequireSession(transitionSessionId);
                var ids = bulkApprove.ParticipantIds.ToHashSet();
                var updated = detail.Participants.Select(x => ids.Contains(x.Id) ? x with { Status = ParticipantStatus.Approved, ConnectionState = ConnectionState.Online } : x).ToArray();
                ReplaceSession(detail with { Participants = updated });
                return (IReadOnlyList<ParticipantDto>)updated.Where(x => ids.Contains(x.Id)).ToArray();
            }

            if (route.Contains("/participants/", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 5, out var routeParticipantId))
            {
                if (route.EndsWith("/approve", StringComparison.OrdinalIgnoreCase))
                {
                    return UpdateParticipant(transitionSessionId, routeParticipantId, x => x with { Status = ParticipantStatus.Approved, ConnectionState = ConnectionState.Online });
                }
                if (route.EndsWith("/reject", StringComparison.OrdinalIgnoreCase))
                {
                    _ = UpdateParticipant(transitionSessionId, routeParticipantId, x => x with { Status = ParticipantStatus.Rejected });
                    return new { rejected = true };
                }
                if (route.EndsWith("/extra-time", StringComparison.OrdinalIgnoreCase) && request is ExtraTimeRequest extraTime)
                {
                    return UpdateParticipant(transitionSessionId, routeParticipantId, x => x with { ExtraTimeMinutes = x.ExtraTimeMinutes + extraTime.Minutes });
                }
                if (route.EndsWith("/heartbeat", StringComparison.OrdinalIgnoreCase))
                {
                    _ = UpdateParticipant(transitionSessionId, routeParticipantId, x => x with { LastSeenUtc = DateTimeOffset.UtcNow, ConnectionState = ConnectionState.Online });
                    return new { heartbeat = true, serverNowUtc = DateTimeOffset.UtcNow };
                }
            }

            if (route.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) && request is SendMessageRequest sendMessage)
            {
                var message = new MessageDto(Guid.NewGuid(), transitionSessionId, null, sendMessage.ReceiverParticipantId, sendMessage.Type, sendMessage.Content, DateTimeOffset.UtcNow);
                Audit("SendMessage", "Message", message.Id.ToString());
                return message;
            }

            if (route.EndsWith("/control-policy/apply", StringComparison.OrdinalIgnoreCase))
            {
                var detail = RequireSession(transitionSessionId);
                var selectedIds = request is ApplyControlPolicyRequest apply && apply.ParticipantIds is { Count: > 0 }
                    ? apply.ParticipantIds.ToHashSet()
                    : detail.Participants.Select(x => x.Id).ToHashSet();
                foreach (var participant in detail.Participants.Where(x => selectedIds.Contains(x.Id)))
                {
                    var existingIndex = deviceStatuses.FindIndex(x => x.ParticipantId == participant.Id);
                    var policyVersion = policies.TryGetValue(transitionSessionId, out var policy) ? policy.Version : 0;
                    var status = new DeviceControlStatusDto(
                        participant.Id,
                        policyVersion,
                        new ControlCapabilitiesDto(true, true, true, true, false),
                        PolicyApplyStatus.Applied,
                        null,
                        DateTimeOffset.UtcNow);
                    if (existingIndex >= 0) deviceStatuses[existingIndex] = status; else deviceStatuses.Add(status);
                }
                Audit("ApplyControlPolicy", "Session", transitionSessionId.ToString());
                return new { applied = selectedIds.Count };
            }
        }

        if (route == "api/v1/submissions/init" && request is InitSubmissionRequest initSubmission)
        {
            var participant = RequireSession(initSubmission.SessionId).Participants.FirstOrDefault(x => x.Id == initSubmission.ParticipantId)
                ?? throw new InvalidOperationException("Không tìm thấy học sinh trong phiên.");
            var submissionId = Guid.NewGuid();
            var attempt = submissions.Where(x => x.ParticipantId == participant.Id).Select(x => x.AttemptNumber).DefaultIfEmpty(0).Max() + 1;
            var chunkSize = settings.ChunkSizeBytes;
            var files = initSubmission.Files.Select(x =>
            {
                var fileId = Guid.NewGuid();
                var chunks = Math.Max(1, (int)Math.Ceiling(x.SizeBytes / (double)chunkSize));
                return new SubmissionFileDto(fileId, x.Name, x.SizeBytes, x.Sha256, x.MimeType, chunks, Array.Empty<int>(), TransferStatus.Running, null);
            }).ToArray();
            var session = RequireSession(initSubmission.SessionId);
            var deadline = session.Summary.EffectiveDeadlineUtc ?? DateTimeOffset.UtcNow.AddMinutes(60);
            var submission = new SubmissionSummaryDto(
                submissionId,
                initSubmission.SessionId,
                initSubmission.ParticipantId,
                participant.StudentCode,
                participant.DisplayName,
                attempt,
                SubmissionStatus.Uploading,
                initSubmission.ClientSubmittedAtUtc,
                null,
                deadline,
                false,
                null,
                true,
                files);
            submissions.Add(submission);
            UpdateParticipant(initSubmission.SessionId, initSubmission.ParticipantId, x => x with { SubmissionStatus = SubmissionStatus.Uploading });
            Audit("InitSubmission", "Submission", submissionId.ToString());
            return new InitSubmissionResponse(
                submissionId,
                attempt,
                chunkSize,
                files.Select(x => new ChunkPlanDto(x.Id, x.TotalChunks, Enumerable.Range(0, x.TotalChunks).ToArray())).ToArray(),
                deadline);
        }

        if (route.StartsWith("api/v1/submissions/", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var postSubmissionId))
        {
            if (route.EndsWith("/finalize", StringComparison.OrdinalIgnoreCase))
            {
                var index = submissions.FindIndex(x => x.Id == postSubmissionId);
                if (index < 0) throw new InvalidOperationException("Không tìm thấy bài nộp.");
                var current = submissions[index];
                var receivedAt = DateTimeOffset.UtcNow;
                var isLate = receivedAt > current.DeadlineUtc;
                var receiptCode = "RC-" + Random.Shared.Next(100000, 999999);
                var status = isLate ? SubmissionStatus.LateSubmitted : SubmissionStatus.Submitted;
                var completedFiles = current.Files.Select(x => x with { ReceivedChunks = Enumerable.Range(0, x.TotalChunks).ToArray(), TransferStatus = TransferStatus.Completed }).ToArray();
                submissions[index] = current with
                {
                    Status = status,
                    ServerReceivedAtUtc = receivedAt,
                    IsLate = isLate,
                    ReceiptCode = receiptCode,
                    Files = completedFiles
                };
                var descriptors = completedFiles.Select(x => new FileDescriptorDto(x.Id, x.Name, x.SizeBytes, x.Sha256, x.MimeType)).ToArray();
                receipts[postSubmissionId] = new ReceiptDto(postSubmissionId, receiptCode, "mock-signature-" + postSubmissionId.ToString("N"), receivedAt, isLate, descriptors);
                UpdateParticipant(current.SessionId, current.ParticipantId, x => x with { SubmissionStatus = status });
                grades[postSubmissionId] = NewGrade(postSubmissionId, GradingStatus.NotGraded);
                Audit("FinalizeSubmission", "Submission", postSubmissionId.ToString());
                return new FinalizeSubmissionResponse(status, receivedAt, isLate, receiptCode, receipts[postSubmissionId].Signature, descriptors);
            }

            if (route.EndsWith("/reject", StringComparison.OrdinalIgnoreCase))
            {
                var index = submissions.FindIndex(x => x.Id == postSubmissionId);
                if (index >= 0) submissions[index] = submissions[index] with { Status = SubmissionStatus.Rejected };
                Audit("RejectSubmission", "Submission", postSubmissionId.ToString());
                return new { rejected = true };
            }
        }

        if (route.StartsWith("api/v1/participants/", StringComparison.OrdinalIgnoreCase) && route.EndsWith("/allow-resubmit", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var resubmitParticipantId))
        {
            Audit("AllowResubmit", "Participant", resubmitParticipantId.ToString());
            return new { allowed = true };
        }

        if (route == "api/v1/exports" && request is CreateExportRequest exportRequest)
        {
            var job = new ExportJobDto(
                Guid.NewGuid(),
                exportRequest.SessionId,
                ExportStatus.Completed,
                100,
                $"ExamTransfer-{exportRequest.SessionId:N}.zip",
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            exports.Add(job);
            Audit("CreateExport", "Export", job.Id.ToString());
            return job;
        }

        if (route.StartsWith("api/v1/exports/", StringComparison.OrdinalIgnoreCase) && route.EndsWith("/cancel", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var cancelExportId))
        {
            var index = exports.FindIndex(x => x.Id == cancelExportId);
            if (index >= 0) exports[index] = exports[index] with { Status = ExportStatus.Cancelled, Error = "Đã hủy theo yêu cầu." };
            return new { cancelled = true };
        }

        if (route.StartsWith("api/v1/grading/submissions/", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 4, out var gradingSubmissionId))
        {
            var current = grades.TryGetValue(gradingSubmissionId, out var existing) ? existing : NewGrade(gradingSubmissionId, GradingStatus.NotGraded);
            if (route.EndsWith("/return", StringComparison.OrdinalIgnoreCase))
            {
                current = current with { Status = GradingStatus.Returned, ReturnedAtUtc = DateTimeOffset.UtcNow, RowVersion = NextVersion(current.RowVersion) };
                grades[gradingSubmissionId] = current;
                Audit("ReturnGrade", "Submission", gradingSubmissionId.ToString());
                return current;
            }
            if (route.EndsWith("/reopen", StringComparison.OrdinalIgnoreCase))
            {
                current = current with { Status = GradingStatus.InProgress, ReturnedAtUtc = null, RowVersion = NextVersion(current.RowVersion) };
                grades[gradingSubmissionId] = current;
                Audit("ReopenGrade", "Submission", gradingSubmissionId.ToString());
                return current;
            }
        }

        if (route.StartsWith("api/v1/violations/", StringComparison.OrdinalIgnoreCase) && route.EndsWith("/acknowledge", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var violationId))
        {
            var index = violations.FindIndex(x => x.Id == violationId);
            if (index >= 0) violations[index] = violations[index] with { HandledAtUtc = DateTimeOffset.UtcNow, HandledBy = Guid.NewGuid() };
            return new { acknowledged = true };
        }

        if (route == "api/v1/backups" && request is CreateBackupRequest createBackup)
        {
            var backup = new BackupDto(
                Guid.NewGuid(),
                "ExamTransfer-backup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmm") + ".etbak",
                createBackup.IncludeFiles ? 18_400_000 : 2_100_000,
                Guid.NewGuid().ToString("N"),
                ContractInfo.SchemaVersion,
                createBackup.Encrypt,
                BackupStatus.Ready,
                DateTimeOffset.UtcNow);
            backups.Add(backup);
            Audit("CreateBackup", "Backup", backup.Id.ToString());
            return backup;
        }

        if (route.StartsWith("api/v1/backups/", StringComparison.OrdinalIgnoreCase) && TryGuidAt(route, 3, out var backupId))
        {
            var index = backups.FindIndex(x => x.Id == backupId);
            if (index < 0) throw new InvalidOperationException("Không tìm thấy bản sao lưu.");
            if (route.EndsWith("/validate", StringComparison.OrdinalIgnoreCase))
            {
                backups[index] = backups[index] with { Status = BackupStatus.Ready };
                return backups[index];
            }
            if (route.EndsWith("/restore", StringComparison.OrdinalIgnoreCase))
            {
                backups[index] = backups[index] with { Status = BackupStatus.RestorePending };
                Audit("ScheduleRestore", "Backup", backupId.ToString());
                return new RestoreScheduledDto(backupId, true, "Khôi phục sẽ được áp dụng sau khi khởi động lại dịch vụ.");
            }
        }

        if (route == "api/v1/cloud/auth/login"
            && request is LoginRequest login)
        {
            cloudSession = new CloudSessionDto(
                true,
                Guid.NewGuid().ToString(),
                login.Email,
                DateTimeOffset.UtcNow.AddHours(1),
                settings.OrganizationId,
                "Teacher");
            settings = settings with
            {
                CloudAuthenticated = true,
                CloudAuthenticatedEmail = login.Email,
                CloudConfigurationStatus = "Sẵn sàng"
            };
            return cloudSession;
        }

        if (route == "api/v1/cloud/auth/logout")
        {
            cloudSession = new CloudSessionDto(
                false, null, null, null, settings.OrganizationId, null);
            settings = settings with
            {
                CloudAuthenticated = false,
                CloudAuthenticatedEmail = null,
                CloudConfigurationStatus = "Chưa đăng nhập"
            };
            return new { loggedOut = true };
        }

        if (route == "api/v1/cloud/auth/refresh")
        {
            return cloudSession;
        }

        if (route == "api/v1/cloud/sync")
        {
            Audit("CloudSyncRequested", "Cloud", null);
            return new { queued = true, pendingItems = settings.CloudEnabled ? 3 : 0 };
        }

        return new { done = true, updatedAtUtc = DateTimeOffset.UtcNow };
    }

    private SessionDetailDto Transition(Guid sessionId, SessionStatus status)
    {
        var current = RequireSession(sessionId);
        var now = DateTimeOffset.UtcNow;
        var start = status == SessionStatus.InProgress && current.Summary.StartTimeUtc is null ? now : current.Summary.StartTimeUtc;
        var end = status == SessionStatus.Finished ? now : current.Summary.EndTimeUtc;
        var deadline = start.HasValue ? start.Value.AddMinutes(RequireExam(current.Summary.ExamId).DurationMinutes) : current.Summary.EffectiveDeadlineUtc;
        var updated = current with
        {
            Summary = current.Summary with
            {
                Status = status,
                ServerNowUtc = now,
                StartTimeUtc = start,
                EndTimeUtc = end,
                EffectiveDeadlineUtc = deadline,
                Sequence = current.Summary.Sequence + 1,
                RowVersion = NextVersion(current.Summary.RowVersion)
            }
        };
        ReplaceSession(updated);
        Audit("Session" + status, "Session", sessionId.ToString());
        return Recalculate(updated);
    }

    private ParticipantDto UpdateParticipant(Guid sessionId, Guid participantId, Func<ParticipantDto, ParticipantDto> update)
    {
        var session = RequireSession(sessionId);
        var participants = session.Participants.ToArray();
        var index = Array.FindIndex(participants, x => x.Id == participantId);
        if (index < 0) throw new InvalidOperationException("Không tìm thấy người tham gia.");
        participants[index] = update(participants[index]);
        ReplaceSession(session with { Participants = participants });
        return participants[index];
    }

    private void ReplaceSession(SessionDetailDto detail)
    {
        var index = sessions.FindIndex(x => x.Summary.Id == detail.Summary.Id);
        if (index < 0) sessions.Add(Recalculate(detail)); else sessions[index] = Recalculate(detail);
    }

    private SessionDetailDto Recalculate(SessionDetailDto detail)
    {
        var participants = detail.Participants;
        var counts = new SessionCountsDto(
            participants.Count,
            participants.Count(x => x.Status == ParticipantStatus.PendingApproval),
            participants.Count(x => x.Status == ParticipantStatus.Approved),
            participants.Count(x => x.ConnectionState == ConnectionState.Online),
            participants.Count(x => x.SubmissionStatus is SubmissionStatus.Submitted or SubmissionStatus.LateSubmitted),
            participants.Count(x => x.SubmissionStatus == SubmissionStatus.Uploading),
            participants.Count(x => x.ConnectionState != ConnectionState.Online));
        return detail with { Summary = detail.Summary with { Counts = counts, ServerNowUtc = DateTimeOffset.UtcNow } };
    }

    private ClassDetailDto RequireClass(Guid id) => classes.FirstOrDefault(x => x.Id == id)
        ?? throw new InvalidOperationException("Không tìm thấy lớp học.");

    private ExamDetailDto RequireExam(Guid id) => exams.FirstOrDefault(x => x.Id == id)
        ?? throw new InvalidOperationException("Không tìm thấy bài kiểm tra.");

    private SessionDetailDto RequireSession(Guid id) => sessions.FirstOrDefault(x => x.Summary.Id == id)
        ?? throw new InvalidOperationException("Không tìm thấy phòng thi.");

    private static ClassSummaryDto ToSummary(ClassDetailDto value) =>
        new(value.Id, value.Name, value.Code, value.SchoolYear, value.Status, value.Students.Count, value.RowVersion);

    private static ExamSummaryDto ToSummary(ExamDetailDto value) =>
        new(value.Id, value.ClassId, value.Title, value.Subject, value.DurationMinutes, value.Status, value.Version, value.Files.Count, value.RowVersion);

    private static GradeDto NewGrade(Guid submissionId, GradingStatus status) =>
        new(
            submissionId,
            status,
            null,
            10,
            new[]
            {
                new RubricScoreDto("requirements", "Đúng yêu cầu", 0, 4, null, 1),
                new RubricScoreDto("logic", "Thuật toán và xử lý", 0, 4, null, 2),
                new RubricScoreDto("quality", "Trình bày và chất lượng", 0, 2, null, 3)
            },
            null,
            Array.Empty<FileDescriptorDto>(),
            null,
            "1");

    private static ParticipantDto NewParticipant(Guid sessionId, string code, string name, ParticipantStatus status, SubmissionStatus submissionStatus) =>
        new(
            Guid.NewGuid(),
            sessionId,
            code,
            name,
            "device-" + code,
            "LAB-PC-" + code[^1],
            "192.168.1." + Random.Shared.Next(20, 240),
            "1.0.0",
            status,
            DateTimeOffset.UtcNow,
            DownloadStatus.Completed,
            submissionStatus,
            0,
            ConnectionState.Online);

    private static SessionSummaryDto NewSessionSummary(Guid id, Guid examId, string title, string roomCode, SessionStatus status, IReadOnlyList<ParticipantDto> participants)
    {
        var counts = new SessionCountsDto(
            participants.Count,
            participants.Count(x => x.Status == ParticipantStatus.PendingApproval),
            participants.Count(x => x.Status == ParticipantStatus.Approved),
            participants.Count(x => x.ConnectionState == ConnectionState.Online),
            participants.Count(x => x.SubmissionStatus is SubmissionStatus.Submitted or SubmissionStatus.LateSubmitted),
            participants.Count(x => x.SubmissionStatus == SubmissionStatus.Uploading),
            participants.Count(x => x.ConnectionState != ConnectionState.Online));
        return new SessionSummaryDto(
            id,
            examId,
            title,
            roomCode,
            status,
            DateTimeOffset.UtcNow,
            null,
            null,
            DateTimeOffset.UtcNow.AddMinutes(65),
            counts,
            1,
            "1");
    }

    private void Audit(string action, string entityType, string? entityId) =>
        audits.Add(new AuditLogDto(
            Guid.NewGuid(),
            sessions.LastOrDefault()?.Summary.Id,
            "teacher",
            action,
            entityType,
            entityId,
            "127.0.0.1",
            null,
            null,
            "mock-trace-" + Guid.NewGuid().ToString("N")[..8],
            DateTimeOffset.UtcNow));

    private static string Normalize(string path)
    {
        var normalized = path.Trim().TrimStart('/');
        var queryIndex = normalized.IndexOf('?');
        return queryIndex >= 0 ? normalized[..queryIndex] : normalized;
    }

    private static bool TryGuidAt(string route, int index, out Guid value)
    {
        value = Guid.Empty;

        var segments = route.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);

        return index >= 0
            && index < segments.Length
            && Guid.TryParse(segments[index], out value);
    }

    private static T Cast<T>(object? value)
    {
        if (value is T typed) return typed;
        if (value is null) return default!;
        throw new InvalidCastException($"Mock route returned {value.GetType().Name}, but {typeof(T).Name} was requested.");
    }

    private static Task<ApiResponse<T>?> Result<T>(T value) =>
        Task.FromResult<ApiResponse<T>?>(ApiResponse<T>.Ok(value, "mock-trace-" + Guid.NewGuid().ToString("N")[..8]));

    private static PagedResult<T> Page<T>(IReadOnlyList<T> items) =>
        new(items, 1, Math.Max(20, items.Count), items.Count);

    private static string NextVersion(string rowVersion) =>
        int.TryParse(rowVersion, out var parsed) ? (parsed + 1).ToString() : Guid.NewGuid().ToString("N");

    private static async Task WriteMockFileAsync(string destinationPath, string content, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? AppContext.BaseDirectory);
        progress?.Report(10);
        await File.WriteAllTextAsync(destinationPath, content, Encoding.UTF8, ct);
        progress?.Report(100);
    }

    private sealed record PendingFileUpload(Guid ExamId, string Name, long SizeBytes, string Sha256, string MimeType);
}
