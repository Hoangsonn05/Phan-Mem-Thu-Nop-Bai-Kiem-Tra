using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Infrastructure;

public sealed class SupabasePublicCloudClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly string? url = Environment.GetEnvironmentVariable("EXAMTRANSFER_SUPABASE_URL")?.TrimEnd('/');
    private readonly string? key = Environment.GetEnvironmentVariable("EXAMTRANSFER_SUPABASE_PUBLISHABLE_KEY");
    private string? accessToken;
    private string? refreshToken;
    private DateTimeOffset expiresAtUtc;

    public bool Configured => Uri.TryCreate(url, UriKind.Absolute, out _) && !string.IsNullOrWhiteSpace(key);
    public bool Authenticated => !string.IsNullOrWhiteSpace(accessToken);
    public string? AccessToken => accessToken;

    public async Task LoginAsync(string account, string password, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var domain = Environment.GetEnvironmentVariable("EXAMTRANSFER_STUDENT_EMAIL_DOMAIN")
            ?? "students.examtransfer.local";
        var email = account.Contains('@') ? account.Trim() : $"{account.Trim()}@{domain.Trim().TrimStart('@')}";
        using var request = ProjectRequest(HttpMethod.Post, "/auth/v1/token?grant_type=password", false);
        request.Content = JsonContent.Create(new { email = email.ToLowerInvariant(), password });
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "Supabase Auth", cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        accessToken = document.RootElement.GetProperty("access_token").GetString();
        refreshToken = document.RootElement.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null;
        var seconds = document.RootElement.TryGetProperty("expires_in", out var expiry) ? expiry.GetInt32() : 3600;
        expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(seconds);
    }

    public async Task<PublicEnrollmentState> RequestEnrollmentAsync(string enrollmentCode, string studentCode, CancellationToken cancellationToken)
    {
        var requestId = await RpcAsync<Guid>("request_public_class_enrollment", new
        {
            p_enrollment_code = enrollmentCode,
            p_student_code = studentCode
        }, cancellationToken);
        using var request = ProjectRequest(HttpMethod.Get,
            $"/rest/v1/class_enrollment_requests?select=id,status&id=eq.{requestId}&limit=1");
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "PublicCloud enrollment status", cancellationToken);
        var rows = JsonSerializer.Deserialize<List<EnrollmentRow>>(
            await response.Content.ReadAsStringAsync(cancellationToken), Json) ?? [];
        if (rows.Count != 1) throw new InvalidDataException("PublicCloud enrollment status was not found.");
        return new PublicEnrollmentState(rows[0].Id, rows[0].Status);
    }

    public async Task<PublicCloudJoinResult> JoinByRoomCodeAsync(
        string roomCode,
        string deviceId,
        string machineName,
        string appVersion,
        CancellationToken cancellationToken)
    {
        await EnsureFreshSessionAsync(cancellationToken);
        using var request = ProjectRequest(HttpMethod.Get,
            $"/rest/v1/exam_sessions?select=id,exam_id,room_code,status&access_mode=eq.PublicCloud&room_code=eq.{Uri.EscapeDataString(roomCode)}&limit=2");
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "PublicCloud session lookup", cancellationToken);
        var sessions = JsonSerializer.Deserialize<List<PublicSessionRow>>(
            await response.Content.ReadAsStringAsync(cancellationToken), Json) ?? [];
        if (sessions.Count != 1)
            throw new InvalidOperationException("Không tìm thấy đúng một phòng PublicCloud khả dụng cho tài khoản này.");
        var session = sessions[0];
        var participantId = await RpcAsync<Guid>("join_public_session", new
        {
            p_session_id = session.Id,
            p_device_id = deviceId,
            p_machine_name = machineName,
            p_app_version = appVersion,
            p_capability_json = new { platform = Environment.OSVersion.Platform.ToString() }
        }, cancellationToken);
        using var participantRequest = ProjectRequest(HttpMethod.Get,
            $"/rest/v1/session_participants?select=status&id=eq.{participantId}&limit=1");
        using var participantResponse = await http.SendAsync(participantRequest, cancellationToken);
        await EnsureSuccessAsync(participantResponse, "PublicCloud participant snapshot", cancellationToken);
        var participants = JsonSerializer.Deserialize<List<ParticipantStatusRow>>(
            await participantResponse.Content.ReadAsStringAsync(cancellationToken), Json) ?? [];
        var status = participants.Count == 1
            && Enum.TryParse<ParticipantStatus>(participants[0].Status, true, out var parsed)
                ? parsed
                : ParticipantStatus.PendingApproval;
        return new PublicCloudJoinResult(session.Id, session.ExamId, participantId, status, accessToken!);
    }

    public async Task<ParticipantStatus> GetParticipantStatusAsync(Guid participantId, CancellationToken cancellationToken)
    {
        await EnsureFreshSessionAsync(cancellationToken);
        using var request = ProjectRequest(HttpMethod.Get,
            $"/rest/v1/session_participants?select=status&id=eq.{participantId}&limit=1");
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "PublicCloud participant snapshot", cancellationToken);
        var rows = JsonSerializer.Deserialize<List<ParticipantStatusRow>>(
            await response.Content.ReadAsStringAsync(cancellationToken), Json) ?? [];
        if (rows.Count != 1 || !Enum.TryParse<ParticipantStatus>(rows[0].Status, true, out var status))
            throw new InvalidDataException("PublicCloud participant snapshot is invalid.");
        return status;
    }

    public async Task<PublicExamFileUrl> GetExamFileUrlAsync(Guid sessionId, Guid fileId, CancellationToken cancellationToken)
    {
        await EnsureFreshSessionAsync(cancellationToken);
        using var request = ProjectRequest(HttpMethod.Post, "/functions/v1/get-public-exam-file-url");
        request.Content = JsonContent.Create(new { sessionId, fileId });
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "PublicCloud signed exam URL", cancellationToken);
        return (await response.Content.ReadFromJsonAsync<PublicExamFileUrl>(Json, cancellationToken))
            ?? throw new InvalidDataException("Signed URL response is empty.");
    }

    public async Task<IReadOnlyList<FileDescriptorDto>> ListExamFilesAsync(Guid examId, CancellationToken cancellationToken)
    {
        await EnsureFreshSessionAsync(cancellationToken);
        using var request = ProjectRequest(HttpMethod.Get,
            $"/rest/v1/exam_files?select=id,name,size_bytes,sha256,mime_type&exam_id=eq.{examId}&order=created_at.asc");
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "PublicCloud exam manifest", cancellationToken);
        var rows = JsonSerializer.Deserialize<List<ExamFileRow>>(
            await response.Content.ReadAsStringAsync(cancellationToken), Json) ?? [];
        return rows.Select(x => new FileDescriptorDto(x.Id, x.Name, x.SizeBytes, x.Sha256,
            x.MimeType ?? "application/octet-stream")).ToList();
    }

    public async Task<PublicSubmissionPlan> InitSubmissionAsync(
        Guid sessionId,
        string idempotencyKey,
        string fileName,
        long sizeBytes,
        string sha256,
        CancellationToken cancellationToken)
    {
        var submissionId = await RpcAsync<Guid>("init_public_submission", new
        {
            p_session_id = sessionId,
            p_idempotency_key = idempotencyKey,
            p_file_name = fileName,
            p_size_bytes = sizeBytes,
            p_sha256 = sha256
        }, cancellationToken);
        using var request = ProjectRequest(HttpMethod.Get,
            $"/rest/v1/submission_files?select=id,cloud_object_path&submission_id=eq.{submissionId}&source_mode=eq.PublicCloud&limit=2");
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "PublicCloud submission file plan", cancellationToken);
        var rows = JsonSerializer.Deserialize<List<SubmissionFilePlanRow>>(
            await response.Content.ReadAsStringAsync(cancellationToken), Json) ?? [];
        if (rows.Count != 1 || string.IsNullOrWhiteSpace(rows[0].CloudObjectPath))
            throw new InvalidDataException("PublicCloud did not return exactly one immutable archive plan.");
        return new PublicSubmissionPlan(submissionId, rows[0].Id, rows[0].CloudObjectPath);
    }

    public async Task UploadSubmissionArchiveAsync(PublicSubmissionPlan plan, string filePath, CancellationToken cancellationToken)
    {
        await EnsureFreshSessionAsync(cancellationToken);
        var encodedPath = plan.CloudObjectPath.Split('/').Select(Uri.EscapeDataString);
        using var request = ProjectRequest(HttpMethod.Post,
            "/storage/v1/object/public-submission-archives/" + string.Join('/', encodedPath));
        request.Headers.TryAddWithoutValidation("x-upsert", "false");
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        request.Content = new StreamContent(stream);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentLength = stream.Length;
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        // A retry after an uncertain response may find the immutable object
        // already present. Verification below remains the source of truth.
        if (response.StatusCode != HttpStatusCode.Conflict)
            await EnsureSuccessAsync(response, "PublicCloud archive upload", cancellationToken);
    }

    public async Task<ReceiptDto> VerifyAndFinalizeSubmissionAsync(
        PublicSubmissionPlan plan,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await EnsureFreshSessionAsync(cancellationToken);
        using var request = ProjectRequest(HttpMethod.Post, "/functions/v1/verify-public-submission-archive");
        request.Content = JsonContent.Create(new { submissionId = plan.SubmissionId, idempotencyKey });
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "PublicCloud archive verification", cancellationToken);

        using var snapshotRequest = ProjectRequest(HttpMethod.Get,
            $"/rest/v1/submissions?select=id,receipt_code,receipt_signature,server_received_at,is_late,submission_files(id,name,size_bytes,sha256,mime_type)&id=eq.{plan.SubmissionId}&limit=1");
        using var snapshotResponse = await http.SendAsync(snapshotRequest, cancellationToken);
        await EnsureSuccessAsync(snapshotResponse, "PublicCloud receipt snapshot", cancellationToken);
        var rows = JsonSerializer.Deserialize<List<ReceiptRow>>(
            await snapshotResponse.Content.ReadAsStringAsync(cancellationToken), Json) ?? [];
        if (rows.Count != 1 || string.IsNullOrWhiteSpace(rows[0].ReceiptCode)
            || string.IsNullOrWhiteSpace(rows[0].ReceiptSignature) || rows[0].ServerReceivedAt is null)
            throw new InvalidDataException("PublicCloud finalize succeeded without a complete receipt snapshot.");
        var complete = rows[0];
        return new ReceiptDto(complete.Id, complete.ReceiptCode!, complete.ReceiptSignature!,
            complete.ServerReceivedAt!.Value, complete.IsLate,
            complete.SubmissionFiles.Select(x => new FileDescriptorDto(x.Id, x.Name, x.SizeBytes, x.Sha256,
                x.MimeType ?? "application/octet-stream")).ToList());
    }

    public async Task<QuizAttemptDto> StartQuizAttemptAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var attemptId = await RpcAsync<Guid>("start_public_quiz_attempt", new
        {
            p_session_id = sessionId,
            p_idempotency_key = $"start-{sessionId:N}"
        }, cancellationToken);
        return await GetQuizAttemptAsync(attemptId, cancellationToken);
    }

    public async Task<SyncQuizAnswersResultDto> SaveQuizAnswersAsync(
        Guid attemptId,
        IReadOnlyList<QuizAnswerDto> answers,
        CancellationToken cancellationToken)
    {
        var accepted = new List<QuizAnswerDto>(answers.Count);
        foreach (var answer in answers.OrderBy(x => x.QuestionId))
        {
            var revision = await RpcAsync<long>("save_public_quiz_answers", new
            {
                p_attempt_id = attemptId,
                p_question_id = answer.QuestionId,
                p_choice_ids = answer.ChoiceIds,
                p_revision = answer.Revision,
                p_client_updated_at = answer.ClientUpdatedAtUtc
            }, cancellationToken);
            accepted.Add(answer with { Revision = revision });
        }
        return new SyncQuizAnswersResultDto(attemptId, accepted, DateTimeOffset.UtcNow);
    }

    public async Task<QuizAttemptDto> FinalizeQuizAttemptAsync(
        Guid attemptId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        _ = await RpcAsync<decimal>("finalize_public_quiz_attempt", new
        {
            p_attempt_id = attemptId,
            p_idempotency_key = idempotencyKey
        }, cancellationToken);
        return await GetQuizAttemptAsync(attemptId, cancellationToken);
    }

    private async Task<QuizAttemptDto> GetQuizAttemptAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        using var request = ProjectRequest(HttpMethod.Get,
            $"/rest/v1/quiz_attempts?select=id,session_id,participant_id,status,exam_version,started_at,deadline_at,finalized_at,score,max_score,snapshot_json,quiz_answers(question_id,choice_ids,revision,client_updated_at)&id=eq.{attemptId}&limit=1");
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "PublicCloud quiz snapshot", cancellationToken);
        var rows = JsonSerializer.Deserialize<List<QuizAttemptRow>>(
            await response.Content.ReadAsStringAsync(cancellationToken), Json) ?? [];
        if (rows.Count != 1) throw new InvalidDataException("PublicCloud quiz attempt was not found.");
        var row = rows[0];
        var questions = JsonSerializer.Deserialize<List<QuizSnapshotQuestion>>(row.SnapshotJson.GetRawText(), Json) ?? [];
        return new QuizAttemptDto(row.Id, row.SessionId, row.ParticipantId,
            Enum.Parse<QuizAttemptStatus>(row.Status, true), row.ExamVersion,
            row.StartedAt, row.DeadlineAt, row.FinalizedAt, row.Score, row.MaxScore,
            questions.Select(q => new QuizQuestionDto(q.Id, q.QuestionText, q.SortOrder, q.Points, q.Multiple,
                q.Choices.Select(c => new QuizChoiceDto(c.Id, c.ChoiceText, c.SortOrder)).ToList())).ToList(),
            row.QuizAnswers.Select(a => new QuizAnswerDto(a.QuestionId, a.ChoiceIds, a.Revision, a.ClientUpdatedAt)).ToList());
    }

    public async Task DownloadVerifiedAsync(PublicExamFileUrl file, string destinationPath, CancellationToken cancellationToken)
    {
        var partial = destinationPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (offset > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            File.Delete(partial);
            offset = 0;
        }
        response.EnsureSuccessStatusCode();
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Append,
                         FileAccess.Write, FileShare.None, 128 * 1024, true))
            await input.CopyToAsync(output, cancellationToken);
        if (new FileInfo(partial).Length != file.SizeBytes)
            throw new InvalidDataException("Downloaded exam size does not match metadata.");
        await using var verify = File.OpenRead(partial);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken)).ToLowerInvariant();
        if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Downloaded exam SHA-256 does not match metadata.");
        File.Move(partial, destinationPath, true);
    }

    public async Task<T> RpcAsync<T>(string name, object payload, CancellationToken cancellationToken)
    {
        await EnsureFreshSessionAsync(cancellationToken);
        using var request = ProjectRequest(HttpMethod.Post, $"/rest/v1/rpc/{name}");
        request.Content = JsonContent.Create(payload, options: Json);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, $"PublicCloud RPC {name}", cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<T>(content, Json)
            ?? throw new InvalidDataException($"RPC {name} returned an empty response.");
    }

    private async Task EnsureFreshSessionAsync(CancellationToken cancellationToken)
    {
        if (expiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(2)) return;
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new InvalidOperationException("Phiên Supabase đã hết hạn; hãy đăng nhập lại.");
        using var request = ProjectRequest(HttpMethod.Post, "/auth/v1/token?grant_type=refresh_token", false);
        request.Content = JsonContent.Create(new { refresh_token = refreshToken });
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "Supabase refresh", cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        accessToken = document.RootElement.GetProperty("access_token").GetString();
        refreshToken = document.RootElement.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : refreshToken;
        expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(document.RootElement.TryGetProperty("expires_in", out var expiry) ? expiry.GetInt32() : 3600);
    }

    private HttpRequestMessage ProjectRequest(HttpMethod method, string path, bool userToken = true)
    {
        EnsureConfigured();
        var request = new HttpRequestMessage(method, url + path);
        request.Headers.TryAddWithoutValidation("apikey", key);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken ? accessToken : key);
        return request;
    }

    private void EnsureConfigured()
    {
        if (!Configured) throw new InvalidOperationException(
            "PublicCloud chưa cấu hình EXAMTRANSFER_SUPABASE_URL và EXAMTRANSFER_SUPABASE_PUBLISHABLE_KEY.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"{operation} failed ({(int)response.StatusCode}): {detail}", null, response.StatusCode);
    }

    private sealed record PublicSessionRow(
        Guid Id,
        [property: JsonPropertyName("exam_id")] Guid ExamId,
        [property: JsonPropertyName("room_code")] string RoomCode,
        string Status);
    private sealed record ParticipantStatusRow(string Status);
    private sealed record EnrollmentRow(Guid Id, string Status);
    private sealed record SubmissionFilePlanRow(Guid Id,
        [property: JsonPropertyName("cloud_object_path")] string CloudObjectPath);
    private sealed record ExamFileRow(Guid Id, string Name,
        [property: JsonPropertyName("size_bytes")] long SizeBytes,
        string Sha256,
        [property: JsonPropertyName("mime_type")] string? MimeType);
    private sealed record ReceiptFileRow(Guid Id, string Name,
        [property: JsonPropertyName("size_bytes")] long SizeBytes,
        string Sha256,
        [property: JsonPropertyName("mime_type")] string? MimeType);
    private sealed record ReceiptRow(Guid Id,
        [property: JsonPropertyName("receipt_code")] string? ReceiptCode,
        [property: JsonPropertyName("receipt_signature")] string? ReceiptSignature,
        [property: JsonPropertyName("server_received_at")] DateTimeOffset? ServerReceivedAt,
        [property: JsonPropertyName("is_late")] bool IsLate,
        [property: JsonPropertyName("submission_files")] IReadOnlyList<ReceiptFileRow> SubmissionFiles);
    private sealed record QuizSnapshotChoice(Guid Id, int SortOrder, string ChoiceText);
    private sealed record QuizSnapshotQuestion(Guid Id, int SortOrder, string QuestionText,
        decimal Points, bool Multiple, IReadOnlyList<QuizSnapshotChoice> Choices);
    private sealed record QuizAnswerRow(
        [property: JsonPropertyName("question_id")] Guid QuestionId,
        [property: JsonPropertyName("choice_ids")] IReadOnlyList<Guid> ChoiceIds,
        long Revision,
        [property: JsonPropertyName("client_updated_at")] DateTimeOffset ClientUpdatedAt);
    private sealed record QuizAttemptRow(Guid Id,
        [property: JsonPropertyName("session_id")] Guid SessionId,
        [property: JsonPropertyName("participant_id")] Guid ParticipantId,
        string Status,
        [property: JsonPropertyName("exam_version")] int ExamVersion,
        [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
        [property: JsonPropertyName("deadline_at")] DateTimeOffset DeadlineAt,
        [property: JsonPropertyName("finalized_at")] DateTimeOffset? FinalizedAt,
        decimal? Score,
        [property: JsonPropertyName("max_score")] decimal MaxScore,
        [property: JsonPropertyName("snapshot_json")] JsonElement SnapshotJson,
        [property: JsonPropertyName("quiz_answers")] IReadOnlyList<QuizAnswerRow> QuizAnswers);
}

public sealed record PublicCloudJoinResult(Guid SessionId, Guid ExamId, Guid ParticipantId, ParticipantStatus Status, string AccessToken);
public sealed record PublicExamFileUrl(Uri Url, int ExpiresIn, string FileName, long SizeBytes, string Sha256);
public sealed record PublicSubmissionPlan(Guid SubmissionId, Guid FileId, string CloudObjectPath);
public sealed record PublicEnrollmentState(Guid RequestId, string Status);
