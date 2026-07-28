using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.Services;

public sealed class AppAuthSessionState : ObservableObject
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private CurrentAccountDto? currentAccount;
    private string? accountAccessToken;
    private string? transientAccount;
    private byte[]? protectedTransientPassword;

    public CurrentAccountDto? CurrentAccount
    {
        get => currentAccount;
        private set
        {
            if (!Set(ref currentAccount, value)) return;

            Raise(nameof(IsAuthenticated));
            Raise(nameof(IsTeacher));
            Raise(nameof(IsStudent));
            Raise(nameof(DisplayName));
            Raise(nameof(StudentCode));
            Raise(nameof(DateOfBirthText));
            Raise(nameof(RoleLabel));
            Raise(nameof(MustChangePassword));
        }
    }

    public string? AccountAccessToken
    {
        get => accountAccessToken;
        private set => Set(ref accountAccessToken, value);
    }

    public bool IsAuthenticated =>
        CurrentAccount is not null && !string.IsNullOrWhiteSpace(AccountAccessToken);

    public bool IsTeacher =>
        CurrentAccount?.Role is UserRole.Teacher or UserRole.Admin;

    public bool IsStudent =>
        CurrentAccount?.Role == UserRole.Student;

    public string DisplayName =>
        CurrentAccount?.DisplayName ?? "Chưa đăng nhập";

    public string StudentCode =>
        CurrentAccount?.StudentCode ?? string.Empty;

    public string DateOfBirthText =>
        CurrentAccount?.DateOfBirth?.ToString("dd/MM/yyyy") ?? "Chưa cập nhật";

    public bool MustChangePassword =>
        CurrentAccount?.MustChangePassword == true;

    public string RoleLabel => CurrentAccount?.Role switch
    {
        UserRole.Admin => "Quản trị viên",
        UserRole.Teacher => "Giáo viên",
        UserRole.Student => "Học sinh",
        _ => "Khách"
    };

    public string? TryRestoreAccessToken()
    {
        try
        {
            if (!File.Exists(StorePath)) return null;

            var protectedBytes = File.ReadAllBytes(StorePath);
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

    public void SetTransientCredentials(string account, string password)
    {
        ClearTransientCredentials();
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrEmpty(password))
            return;

        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            transientAccount = account.Trim();
            protectedTransientPassword = ProtectedData.Protect(
                passwordBytes,
                null,
                DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    public bool TryGetTransientCredentials(out string account, out string password)
    {
        account = string.Empty;
        password = string.Empty;
        if (string.IsNullOrWhiteSpace(transientAccount)
            || protectedTransientPassword is null)
            return false;

        byte[]? clearBytes = null;
        try
        {
            clearBytes = ProtectedData.Unprotect(
                protectedTransientPassword,
                null,
                DataProtectionScope.CurrentUser);
            account = transientAccount;
            password = Encoding.UTF8.GetString(clearBytes);
            return password.Length > 0;
        }
        catch (CryptographicException ex)
        {
            FrontendLogger.Log(ex, "AppAuthSessionState.TransientCredentials");
            ClearTransientCredentials();
            return false;
        }
        finally
        {
            if (clearBytes is not null)
                CryptographicOperations.ZeroMemory(clearBytes);
        }
    }

    public void ClearTransientCredentials()
    {
        transientAccount = null;
        if (protectedTransientPassword is not null)
            CryptographicOperations.ZeroMemory(protectedTransientPassword);
        protectedTransientPassword = null;
    }

    public void Clear()
    {
        CurrentAccount = null;
        AccountAccessToken = null;
        ClearTransientCredentials();

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
        Path.Combine(AppProfile.LocalDataRoot, "auth-session.bin");

    private sealed record StoredAuthSession(string AccessToken);
}
