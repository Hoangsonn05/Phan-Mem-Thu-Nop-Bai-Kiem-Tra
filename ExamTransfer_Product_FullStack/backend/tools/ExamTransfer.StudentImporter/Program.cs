using System.Text;

namespace ExamTransfer.StudentImporter;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        try
        {
            var options = ImporterOptions.Parse(args);
            var runtime = options.ResolveRuntimeSettings();
            var supabaseUrl = ResolveSupabaseUrl(runtime.SupabaseUrl);
            var organizationId = ResolveOrganizationId(runtime.OrganizationId);
            var emailDomain = ResolveEmailDomain(runtime.StudentEmailDomain);
            var secretKey = ResolveSecretKey(options.SecretEnvironmentVariable);

            var students = StudentSpreadsheetParser
                .Read(options.FilePath, emailDomain)
                .Skip(options.Skip)
                .Take(options.Limit > 0 ? options.Limit : int.MaxValue)
                .ToList();

            if (students.Count == 0)
                throw new InvalidOperationException("Không còn sinh viên nào sau khi áp dụng --skip/--limit.");

            string? password = null;
            if (!options.DryRun || options.VerifyLogin || options.ResetExistingPassword)
            {
                password = ResolveTemporaryPassword(options.PasswordEnvironmentVariable);
            }

            Console.WriteLine("ExamTransfer Student Importer");
            Console.WriteLine($"File             : {options.FilePath}");
            Console.WriteLine($"Supabase         : {supabaseUrl.Host}");
            Console.WriteLine($"Organization ID  : {organizationId:D}");
            Console.WriteLine($"Email domain     : {emailDomain}");
            Console.WriteLine($"Students selected: {students.Count}");
            Console.WriteLine($"Mode             : {(options.DryRun ? "DRY RUN" : "WRITE")}");
            Console.WriteLine();

            using var client = new SupabaseAdminClient(
                supabaseUrl,
                secretKey,
                runtime.PublishableKey);

            var organizationName = await client.EnsureOrganizationAsync(
                organizationId,
                CancellationToken.None);
            Console.WriteLine($"Organization     : {organizationName}");

            var usersByEmail = await client.GetUsersByEmailAsync(CancellationToken.None);
            var profilesByCode = await client.GetProfilesByStudentCodeAsync(
                organizationId,
                CancellationToken.None);

            var results = new List<ProvisioningResult>();
            foreach (var student in students)
            {
                var result = await ProvisionOneAsync(
                    client,
                    student,
                    organizationId,
                    usersByEmail,
                    profilesByCode,
                    password,
                    options);
                results.Add(result);

                var marker = result.Status == "Succeeded" ? "OK" : "FAIL";
                Console.WriteLine(
                    $"[{marker}] {student.StudentCode} - {student.DisplayName} - " +
                    $"{result.Action} - {result.Message}");
            }

            var reportPath = ImportReportWriter.Write(options.ReportPath, results);
            var succeeded = results.Count(x => x.Status == "Succeeded");
            var failed = results.Count - succeeded;

            Console.WriteLine();
            Console.WriteLine($"Succeeded: {succeeded}");
            Console.WriteLine($"Failed   : {failed}");
            Console.WriteLine($"Report   : {reportPath}");
            Console.WriteLine("Không có mật khẩu hoặc khóa Supabase nào được ghi vào báo cáo.");

            return failed == 0 ? 0 : 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Đã hủy thao tác.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Lỗi: {ex.Message}");
            return 1;
        }
    }

    private static async Task<ProvisioningResult> ProvisionOneAsync(
        SupabaseAdminClient client,
        StudentImportRow student,
        Guid organizationId,
        IReadOnlyDictionary<string, AuthUserSnapshot> usersByEmail,
        IReadOnlyDictionary<string, ProfileSnapshot> profilesByCode,
        string? password,
        ImporterOptions options)
    {
        usersByEmail.TryGetValue(student.TechnicalEmail, out var existingUser);
        profilesByCode.TryGetValue(student.StudentCode, out var existingProfile);

        if (existingUser is not null
            && existingProfile is not null
            && !string.Equals(existingUser.Id, existingProfile.Id, StringComparison.OrdinalIgnoreCase))
        {
            return ProvisioningResult.Failed(
                student,
                "Conflict",
                "Auth user và profile có UUID khác nhau; không tự động sửa để tránh gán nhầm tài khoản.");
        }

        if (existingUser is null && existingProfile is not null)
        {
            return ProvisioningResult.Failed(
                student,
                "Conflict",
                "Đã có profile nhưng không tìm thấy Auth user đúng email kỹ thuật.");
        }

        var action = existingUser is null
            ? "CreateAuthAndProfile"
            : options.ResetExistingPassword
                ? "ResetPasswordAndUpsertProfile"
                : "UpsertProfile";

        if (options.DryRun)
        {
            var dryRunAction = existingUser is null ? "DryRunCreate" : "DryRunUpdate";
            return new ProvisioningResult(
                student.SourceRow,
                student.StudentCode,
                student.DisplayName,
                student.DateOfBirth.ToString("yyyy-MM-dd"),
                student.TechnicalEmail,
                dryRunAction,
                "Succeeded",
                existingUser?.Id ?? existingProfile?.Id,
                "NotRun",
                existingUser is null
                    ? "Sẵn sàng tạo Auth user và profile."
                    : "Tài khoản đã tồn tại; sẵn sàng cập nhật profile.");
        }

        if (string.IsNullOrWhiteSpace(password))
            return ProvisioningResult.Failed(student, action, "Thiếu mật khẩu tạm.");

        AuthUserSnapshot? user = existingUser;
        var createdNow = false;
        try
        {
            if (user is null)
            {
                user = await client.CreateUserAsync(
                    student,
                    organizationId,
                    password,
                    CancellationToken.None);
                createdNow = true;
            }
            else if (options.ResetExistingPassword)
            {
                await client.UpdateExistingUserAsync(
                    user.Id,
                    student,
                    organizationId,
                    password,
                    CancellationToken.None);
            }

            var mustChangePassword = createdNow
                || options.ResetExistingPassword
                || existingProfile?.MustChangePassword == true;

            await client.UpsertProfileAsync(
                user.Id,
                student,
                organizationId,
                mustChangePassword,
                CancellationToken.None);

            var verification = "NotRun";
            if (options.VerifyLogin)
            {
                await client.VerifyLoginAsync(
                    student,
                    password,
                    user.Id,
                    CancellationToken.None);
                verification = "Passed";
            }

            return new ProvisioningResult(
                student.SourceRow,
                student.StudentCode,
                student.DisplayName,
                student.DateOfBirth.ToString("yyyy-MM-dd"),
                student.TechnicalEmail,
                action,
                "Succeeded",
                user.Id,
                verification,
                createdNow
                    ? "Đã tạo Auth user và profile."
                    : options.ResetExistingPassword
                        ? "Đã đặt lại mật khẩu và cập nhật profile."
                        : "Đã cập nhật profile; mật khẩu Auth user được giữ nguyên.");
        }
        catch (Exception ex)
        {
            if (createdNow && user is not null)
            {
                try
                {
                    await client.DeleteUserAsync(user.Id, CancellationToken.None);
                    return ProvisioningResult.Failed(
                        student,
                        action,
                        ex.Message + " Auth user mới đã được hoàn tác.");
                }
                catch (Exception rollbackEx)
                {
                    return ProvisioningResult.Failed(
                        student,
                        action,
                        ex.Message + " Hoàn tác Auth user thất bại: " + rollbackEx.Message);
                }
            }

            return ProvisioningResult.Failed(student, action, ex.Message);
        }
    }

    private static Uri ResolveSupabaseUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
        {
            throw new InvalidOperationException(
                "Thiếu hoặc sai SupabaseUrl. Kiểm tra runtime-settings.json hoặc truyền --supabase-url.");
        }

        return uri;
    }

    private static Guid ResolveOrganizationId(string? value)
    {
        if (!Guid.TryParse(value, out var organizationId))
        {
            throw new InvalidOperationException(
                "Thiếu hoặc sai OrganizationId. Kiểm tra runtime-settings.json hoặc truyền --organization-id.");
        }

        return organizationId;
    }

    private static string ResolveEmailDomain(string? value)
    {
        var domain = value?.Trim().TrimStart('@').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(domain)
            || domain.Any(char.IsWhiteSpace)
            || !domain.Contains('.'))
        {
            throw new InvalidOperationException("StudentEmailDomain không hợp lệ.");
        }

        return domain;
    }

    private static string ResolveSecretKey(string environmentVariable)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(value)
            && !string.Equals(
                environmentVariable,
                "EXAMTRANSFER_SUPABASE_SERVICE_KEY",
                StringComparison.OrdinalIgnoreCase))
        {
            value = Environment.GetEnvironmentVariable("EXAMTRANSFER_SUPABASE_SERVICE_KEY");
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Chưa có secret key trong biến môi trường {environmentVariable}. " +
                "Không ghi secret key vào mã nguồn hoặc file Excel.");
        }

        return value.Trim();
    }

    private static string ResolveTemporaryPassword(string environmentVariable)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(value))
            value = ReadPasswordSecurely("Nhập mật khẩu tạm cho sinh viên: ");

        if (value.Length < 5)
            throw new InvalidOperationException("Mật khẩu tạm phải có ít nhất 5 ký tự.");

        return value;
    }

    private static string ReadPasswordSecurely(string prompt)
    {
        Console.Write(prompt);
        var builder = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                    Console.Write("\b \b");
                }
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
                Console.Write('*');
            }
        }

        return builder.ToString();
    }
}
