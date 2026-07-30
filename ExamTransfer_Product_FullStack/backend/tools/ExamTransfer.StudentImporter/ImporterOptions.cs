using System.Text.Json;

namespace ExamTransfer.StudentImporter;

internal sealed class ImporterOptions
{
    public required string FilePath { get; init; }
    public string? ConfigPath { get; init; }
    public string? SupabaseUrl { get; init; }
    public string? PublishableKey { get; init; }
    public string? OrganizationId { get; init; }
    public string? EmailDomain { get; init; }
    public string SecretEnvironmentVariable { get; init; } = "EXAMTRANSFER_SUPABASE_SECRET_KEY";
    public string PasswordEnvironmentVariable { get; init; } = "EXAMTRANSFER_STUDENT_TEMP_PASSWORD";
    public string? ReportPath { get; init; }
    public int Skip { get; init; }
    public int Limit { get; init; }
    public bool DryRun { get; init; }
    public bool ResetExistingPassword { get; init; }
    public bool VerifyLogin { get; init; }

    public static ImporterOptions Parse(string[] args)
    {
        if (args.Length == 0 || args.Any(x => x is "--help" or "-h" or "/?"))
        {
            PrintHelp();
            Environment.Exit(0);
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var switches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var switchNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--dry-run",
            "--reset-existing-password",
            "--verify-login"
        };

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (switchNames.Contains(token))
            {
                switches.Add(token);
                continue;
            }

            if (!token.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Tham số không hợp lệ: {token}");

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Thiếu giá trị cho tham số {token}.");

            values[token] = args[++i];
        }

        if (!values.TryGetValue("--file", out var filePath) || string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("Bắt buộc truyền --file với đường dẫn file CSV/XLSX.");

        var fullFilePath = Path.GetFullPath(filePath);
        if (!File.Exists(fullFilePath))
            throw new FileNotFoundException("Không tìm thấy file danh sách sinh viên.", fullFilePath);

        var skip = ParseNonNegativeInt(values, "--skip", 0);
        var limit = ParseNonNegativeInt(values, "--limit", 0);

        return new ImporterOptions
        {
            FilePath = fullFilePath,
            ConfigPath = Get(values, "--config"),
            SupabaseUrl = Get(values, "--supabase-url"),
            PublishableKey = Get(values, "--publishable-key"),
            OrganizationId = Get(values, "--organization-id"),
            EmailDomain = Get(values, "--email-domain"),
            SecretEnvironmentVariable = Get(values, "--secret-env")
                ?? "EXAMTRANSFER_SUPABASE_SECRET_KEY",
            PasswordEnvironmentVariable = Get(values, "--password-env")
                ?? "EXAMTRANSFER_STUDENT_TEMP_PASSWORD",
            ReportPath = Get(values, "--report"),
            Skip = skip,
            Limit = limit,
            DryRun = switches.Contains("--dry-run"),
            ResetExistingPassword = switches.Contains("--reset-existing-password"),
            VerifyLogin = switches.Contains("--verify-login")
        };
    }

    public RuntimeCloudSettings ResolveRuntimeSettings()
    {
        var configPath = ConfigPath;
        if (string.IsNullOrWhiteSpace(configPath))
        {
            var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrWhiteSpace(commonData))
            {
                configPath = Path.Combine(
                    commonData,
                    "ExamTransfer",
                    "config",
                    "runtime-settings.json");
            }
        }

        string? configUrl = null;
        string? configPublishableKey = null;
        string? configOrganizationId = null;
        string? configStudentDomain = null;

        if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = document.RootElement;

            if (root.TryGetProperty("Cloud", out var cloud))
            {
                configUrl = ReadString(cloud, "SupabaseUrl");
                configPublishableKey = ReadString(cloud, "PublishableKey")
                    ?? ReadString(cloud, "AnonKey");
                configOrganizationId = ReadString(cloud, "OrganizationId");
            }

            if (root.TryGetProperty("Auth", out var auth))
                configStudentDomain = ReadString(auth, "StudentEmailDomain");
        }

        return new RuntimeCloudSettings(
            SupabaseUrl ?? configUrl,
            PublishableKey ?? configPublishableKey,
            OrganizationId ?? configOrganizationId,
            EmailDomain ?? configStudentDomain ?? "students.examtransfer.local");
    }

    private static string? ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Get(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static int ParseNonNegativeInt(
        Dictionary<string, string> values,
        string key,
        int defaultValue)
    {
        if (!values.TryGetValue(key, out var text))
            return defaultValue;

        if (!int.TryParse(text, out var value) || value < 0)
            throw new ArgumentException($"{key} phải là số nguyên không âm.");

        return value;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
ExamTransfer.StudentImporter

Tạo/cập nhật tài khoản Supabase Auth và public.profiles từ file CSV/XLSX.

Bắt buộc:
  --file <path>                 File danh sách sinh viên.

Tự đọc mặc định từ:
  C:\ProgramData\ExamTransfer\config\runtime-settings.json

Có thể ghi đè:
  --config <path>               File runtime-settings.json khác.
  --supabase-url <url>          URL project Supabase.
  --organization-id <uuid>      Organization ID.
  --publishable-key <key>       Chỉ dùng cho --verify-login.
  --email-domain <domain>       Mặc định students.examtransfer.local.
  --secret-env <name>           Mặc định EXAMTRANSFER_SUPABASE_SECRET_KEY.
  --password-env <name>         Mặc định EXAMTRANSFER_STUDENT_TEMP_PASSWORD.
  --skip <n>                    Bỏ qua n sinh viên hợp lệ đầu tiên.
  --limit <n>                   Chỉ xử lý n sinh viên; 0 = toàn bộ.
  --report <path>               File CSV báo cáo.

Chế độ:
  --dry-run                     Chỉ kiểm tra, không tạo/cập nhật dữ liệu.
  --verify-login                Sau import, thử đăng nhập Supabase bằng mật khẩu tạm.
  --reset-existing-password     Đặt lại mật khẩu cho Auth user đã tồn tại.

Khóa secret và mật khẩu không được truyền trên dòng lệnh. Hãy dùng biến môi
trường hoặc nhập mật khẩu tại lời nhắc bảo mật của chương trình.
""");
    }
}
