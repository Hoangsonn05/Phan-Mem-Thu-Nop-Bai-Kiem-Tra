using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class StudentHomeViewModel
{
    private readonly CurrentAccountDto account;

    public StudentHomeViewModel(AppAuthSessionState authState)
    {
        account = authState.CurrentAccount
            ?? throw new InvalidOperationException("Không tìm thấy phiên đăng nhập học sinh.");

        if (account.Role != UserRole.Student)
            throw new InvalidOperationException("Trang chủ học sinh chỉ dành cho tài khoản Student.");
    }

    public string Greeting => $"Xin chào, {account.DisplayName}";
    public string DisplayName => account.DisplayName;
    public string StudentCode => account.StudentCode ?? account.Username;
    public string DateOfBirthText => account.DateOfBirth?.ToString("dd/MM/yyyy") ?? "Chưa cập nhật";
    public string Username => account.Username;
    public string RoleLabel => "Học sinh";
    public string AccountStatus => "Đang hoạt động";
    public string AccountStatusTone => "success";
    public string DeviceId => account.DeviceId;
    public string SessionExpiresAt => account.ExpiresAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    public bool MustChangePassword => account.MustChangePassword;
    public string PasswordStatus => account.MustChangePassword
        ? "Đang sử dụng mật khẩu tạm"
        : "Mật khẩu đã được thiết lập";
    public string PasswordTone => account.MustChangePassword ? "warning" : "success";
    public string Initials => CreateInitials(account.DisplayName);

    private static string CreateInitials(string displayName)
    {
        var parts = displayName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0) return "SV";
        if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();

        return $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
    }
}
