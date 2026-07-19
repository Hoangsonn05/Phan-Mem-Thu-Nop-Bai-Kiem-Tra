using System.Reflection;
using System.Windows.Input;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class LoginViewModel : ObservableObject
{
    private readonly IBackendClient api;
    private readonly AppAuthSessionState authState;
    private readonly Func<Task> authenticated;
    private string account = string.Empty;
    private string password = string.Empty;
    private bool isBusy;
    private string status = "Học sinh nhập mã sinh viên; giáo viên và quản trị viên nhập email.";
    private string statusTone = "info";

    public LoginViewModel(IBackendClient api, AppAuthSessionState authState, Func<Task> authenticated)
    {
        this.api = api;
        this.authState = authState;
        this.authenticated = authenticated;
        DeviceId = EnsureDeviceId();
        LoginCommand = new AsyncRelayCommand(LoginAsync, CanLogin);
    }

    public string Account
    {
        get => account;
        set
        {
            if (Set(ref account, value))
                RaiseCommand();
        }
    }

    public string Password
    {
        get => password;
        set
        {
            if (Set(ref password, value))
                RaiseCommand();
        }
    }

    public string DeviceId { get; }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (Set(ref isBusy, value))
                RaiseCommand();
        }
    }

    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    public string StatusTone
    {
        get => statusTone;
        private set => Set(ref statusTone, value);
    }

    public ICommand LoginCommand { get; }

    private bool CanLogin() =>
        !IsBusy
        && !string.IsNullOrWhiteSpace(Account)
        && !string.IsNullOrWhiteSpace(Password);

    private async Task LoginAsync()
    {
        if (IsBusy) return;

        try
        {
            IsBusy = true;
            Status = "Đang xác thực tài khoản...";
            StatusTone = "primary";

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = ApiGuard.Require(await api.PostAsync<AccountLoginRequest, AccountLoginResultDto>(
                "api/v1/auth/login",
                new AccountLoginRequest(
                    Account.Trim(),
                    Password,
                    DeviceId,
                    Environment.MachineName,
                    AppVersion),
                cts.Token));

            if (result.RequiresStudentConfirmation)
            {
                throw new InvalidOperationException(
                    "Máy chủ vẫn đang dùng luồng xác nhận sinh viên cũ. Hãy kiểm tra lại bản vá backend giai đoạn 2.");
            }

            if (string.IsNullOrWhiteSpace(result.AccessToken))
                throw new InvalidOperationException("Máy chủ không trả về token đăng nhập.");

            api.SetAccountToken(result.AccessToken);
            var current = ApiGuard.Require(await api.GetAsync<CurrentAccountDto>("api/v1/auth/me", cts.Token));

            if (current.Role == UserRole.Student)
            {
                if (string.IsNullOrWhiteSpace(current.StudentCode))
                    throw new InvalidOperationException("Hồ sơ sinh viên chưa có mã sinh viên.");

                if (current.DateOfBirth is null)
                    throw new InvalidOperationException("Hồ sơ sinh viên chưa có ngày sinh.");
            }

            authState.SetAuthenticated(current, result.AccessToken);
            Password = string.Empty;
            Status = "Đăng nhập thành công.";
            StatusTone = "success";
            await authenticated();
        }
        catch (Exception ex)
        {
            api.SetAccountToken(null);
            FrontendLogger.Log(ex, "LoginViewModel");
            Status = ex.Message;
            StatusTone = "danger";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseCommand() =>
        (LoginCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();

    private static string AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

    private static string EnsureDeviceId()
    {
        var stored = AppServices.Preferences.Get("device-id");
        if (!string.IsNullOrWhiteSpace(stored)) return stored;

        var generated = "ET-" + Guid.NewGuid().ToString("N");
        AppServices.Preferences.Set("device-id", generated);
        return generated;
    }
}
