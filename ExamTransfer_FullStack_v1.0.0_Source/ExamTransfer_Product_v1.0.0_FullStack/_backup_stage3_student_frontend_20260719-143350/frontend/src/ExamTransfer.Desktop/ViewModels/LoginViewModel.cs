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
    private string studentCode = string.Empty;
    private string displayName = string.Empty;
    private string? challengeToken;
    private bool isBusy;
    private bool isConfirmingStudent;
    private string status = "Đăng nhập bằng tài khoản được quản trị viên cấp.";
    private string statusTone = "info";

    public LoginViewModel(IBackendClient api, AppAuthSessionState authState, Func<Task> authenticated)
    {
        this.api = api;
        this.authState = authState;
        this.authenticated = authenticated;
        DeviceId = EnsureDeviceId();
        LoginCommand = new AsyncRelayCommand(LoginAsync, CanLogin);
        ConfirmStudentCommand = new AsyncRelayCommand(ConfirmStudentAsync, CanConfirmStudent);
        BackToLoginCommand = new RelayCommand(BackToLogin);
    }

    public string Account { get => account; set { if (Set(ref account, value)) RaiseCommands(); } }
    public string Password { get => password; set { if (Set(ref password, value)) RaiseCommands(); } }
    public string StudentCode { get => studentCode; set { if (Set(ref studentCode, value)) RaiseCommands(); } }
    public string DisplayName { get => displayName; set { if (Set(ref displayName, value)) RaiseCommands(); } }
    public string DeviceId { get; }
    public bool IsBusy { get => isBusy; private set { if (Set(ref isBusy, value)) RaiseCommands(); } }
    public bool IsConfirmingStudent { get => isConfirmingStudent; private set => Set(ref isConfirmingStudent, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public string StatusTone { get => statusTone; private set => Set(ref statusTone, value); }
    public ICommand LoginCommand { get; }
    public ICommand ConfirmStudentCommand { get; }
    public ICommand BackToLoginCommand { get; }

    private bool CanLogin() =>
        !IsBusy
        && !IsConfirmingStudent
        && !string.IsNullOrWhiteSpace(Account)
        && !string.IsNullOrWhiteSpace(Password);

    private bool CanConfirmStudent() =>
        !IsBusy
        && IsConfirmingStudent
        && !string.IsNullOrWhiteSpace(challengeToken)
        && !string.IsNullOrWhiteSpace(StudentCode)
        && !string.IsNullOrWhiteSpace(DisplayName);

    private async Task LoginAsync()
    {
        await RunAsync(async ct =>
        {
            var result = ApiGuard.Require(await api.PostAsync<AccountLoginRequest, AccountLoginResultDto>(
                "api/v1/auth/login",
                new AccountLoginRequest(Account.Trim(), Password, DeviceId, Environment.MachineName, AppVersion),
                ct));
            Password = string.Empty;

            if (result.RequiresStudentConfirmation)
            {
                challengeToken = result.ChallengeToken;
                StudentCode = result.StudentCode ?? string.Empty;
                DisplayName = result.DisplayName ?? string.Empty;
                IsConfirmingStudent = true;
                Status = "Xác nhận mã sinh viên và họ tên để hoàn tất đăng nhập.";
                StatusTone = "warning";
                RaiseCommands();
                return;
            }

            await CompleteAuthenticationAsync(result, ct);
        });
    }

    private async Task ConfirmStudentAsync()
    {
        await RunAsync(async ct =>
        {
            var result = ApiGuard.Require(await api.PostAsync<StudentIdentityConfirmRequest, AccountLoginResultDto>(
                "api/v1/auth/student/confirm",
                new StudentIdentityConfirmRequest(challengeToken!, StudentCode.Trim(), DisplayName.Trim(), DeviceId, Environment.MachineName),
                ct));
            await CompleteAuthenticationAsync(result, ct);
        });
    }

    private async Task CompleteAuthenticationAsync(AccountLoginResultDto result, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(result.AccessToken))
            throw new InvalidOperationException("Máy chủ không trả về account token.");

        api.SetAccountToken(result.AccessToken);
        var current = ApiGuard.Require(await api.GetAsync<CurrentAccountDto>("api/v1/auth/me", ct));
        authState.SetAuthenticated(current, result.AccessToken);
        Status = "Đăng nhập thành công.";
        StatusTone = "success";
        await authenticated();
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            Status = "Đang xác thực tài khoản...";
            StatusTone = "primary";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await action(cts.Token);
        }
        catch (Exception ex)
        {
            FrontendLogger.Log(ex, "LoginViewModel");
            Status = ex.Message;
            StatusTone = "danger";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BackToLogin()
    {
        IsConfirmingStudent = false;
        challengeToken = null;
        Status = "Đăng nhập bằng tài khoản được quản trị viên cấp.";
        StatusTone = "info";
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        (LoginCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ConfirmStudentCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

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
