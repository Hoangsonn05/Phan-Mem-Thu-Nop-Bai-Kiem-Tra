using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Services;

public sealed class AppAuthSessionState : ObservableObject
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private CurrentAccountDto? currentAccount;
    private string? accountAccessToken;

    public CurrentAccountDto? CurrentAccount
    {
        get => currentAccount;
        private set
        {
            if (Set(ref currentAccount, value))
            {
                Raise(nameof(IsAuthenticated));
                Raise(nameof(IsTeacher));
                Raise(nameof(IsStudent));
                Raise(nameof(DisplayName));
                Raise(nameof(RoleLabel));
            }
        }
    }

    public string? AccountAccessToken
    {
        get => accountAccessToken;
        private set => Set(ref accountAccessToken, value);
    }

    public bool IsAuthenticated => CurrentAccount is not null && !string.IsNullOrWhiteSpace(AccountAccessToken);
    public bool IsTeacher => CurrentAccount?.Role is UserRole.Teacher or UserRole.Admin;
    public bool IsStudent => CurrentAccount?.Role == UserRole.Student;
    public string DisplayName => CurrentAccount?.DisplayName ?? "Chưa đăng nhập";
    public string RoleLabel => CurrentAccount?.Role.ToString() ?? "Guest";

    public string? TryRestoreAccessToken()
    {
        try
        {
            var path = StorePath;
            if (!File.Exists(path)) return null;
            var protectedBytes = File.ReadAllBytes(path);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var stored = JsonSerializer.Deserialize<StoredAuthSession>(bytes, Json);
            return string.IsNullOrWhiteSpace(stored?.AccessToken) ? null : stored.AccessToken;
        }
        catch (Exception ex)
        {
            FrontendLogger.Log(ex, "AppAuthSessionState.Restore");
            Clear();
            return null;
        }
    }

    public void SetAuthenticated(CurrentAccountDto account, string accessToken)
    {
        CurrentAccount = account;
        AccountAccessToken = accessToken;
        Save(accessToken);
    }

    public void Clear()
    {
        CurrentAccount = null;
        AccountAccessToken = null;
        try
        {
            if (File.Exists(StorePath))
                File.Delete(StorePath);
        }
        catch (Exception ex)
        {
            FrontendLogger.Log(ex, "AppAuthSessionState.Clear");
        }
    }

    private static void Save(string accessToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new StoredAuthSession(accessToken), Json);
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(StorePath, protectedBytes);
        }
        catch (Exception ex)
        {
            FrontendLogger.Log(ex, "AppAuthSessionState.Save");
        }
    }

    private static string StorePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ExamTransfer",
            "auth-session.bin");

    private sealed record StoredAuthSession(string AccessToken);
}
