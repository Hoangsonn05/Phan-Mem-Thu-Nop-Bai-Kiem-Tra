using System.Windows.Input;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Infrastructure;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class ChangePasswordViewModel : ObservableObject
{
    private readonly IBackendClient api;
    private readonly AppAuthSessionState authState;
    private readonly Func<Task> completed;
    private readonly Func<
        CurrentAccountDto,
        string,
        string,
        string,
        CancellationToken,
        Task<SupabaseAuthenticatedAccount>> changeStudentPassword;
    private string currentPassword = string.Empty;
    private string newPassword = string.Empty;
    private string confirmPassword = string.Empty;
    private bool isBusy;
    private string status = "Bạn đang dùng mật khẩu tạm. Hãy đổi mật khẩu trước khi sử dụng các chức năng học sinh.";
    private string statusTone = "warning";

    public ChangePasswordViewModel(
        IBackendClient api,
        AppAuthSessionState authState,
        Func<Task> completed,
        Func<
            CurrentAccountDto,
            string,
            string,
            string,
            CancellationToken,
            Task<SupabaseAuthenticatedAccount>>? changeStudentPassword = null)
    {
        this.api = api;
        this.authState = authState;
        this.completed = completed;
        this.changeStudentPassword = changeStudentPassword
            ?? ((student, current, next, confirm, cancellationToken) =>
                AppServices.PublicCloud.ChangeOwnPasswordAsync(
                    student,
                    current,
                    next,
                    confirm,
                    student.DeviceId,
                    cancellationToken));
        ChangePasswordCommand = new AsyncRelayCommand(ChangePasswordAsync, CanSubmit);
    }

    public string DisplayName => authState.DisplayName;
    public string StudentCode => authState.StudentCode;

    public string CurrentPassword
    {
        get => currentPassword;
        set
        {
            if (Set(ref currentPassword, value))
                RaiseCommand();
        }
    }

    public string NewPassword
    {
        get => newPassword;
        set
        {
            if (Set(ref newPassword, value))
                RaiseCommand();
        }
    }

    public string ConfirmPassword
    {
        get => confirmPassword;
        set
        {
            if (Set(ref confirmPassword, value))
                RaiseCommand();
        }
    }

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

    public ICommand ChangePasswordCommand { get; }

    private bool CanSubmit() =>
        !IsBusy
        && !string.IsNullOrWhiteSpace(CurrentPassword)
        && !string.IsNullOrWhiteSpace(NewPassword)
        && !string.IsNullOrWhiteSpace(ConfirmPassword);

    private async Task ChangePasswordAsync()
    {
        if (IsBusy) return;

        try
        {
            IsBusy = true;
            Status = "Đang xác nhận mật khẩu hiện tại và cập nhật mật khẩu mới...";
            StatusTone = "primary";

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            string resultMessage;
            if (authState.IsStudent && authState.CurrentAccount is { } student)
            {
                var account = StudentAccountIdentifier(student);
                var changed = await changeStudentPassword(
                    student,
                    CurrentPassword,
                    NewPassword,
                    ConfirmPassword,
                    cts.Token);
                if (changed.Account.MustChangePassword)
                    throw new PublicCloudApiException(
                        ErrorCodes.PasswordChangeFailed,
                        "Hồ sơ vẫn yêu cầu đổi mật khẩu; ứng dụng chưa mở khóa chức năng học sinh.",
                        System.Net.HttpStatusCode.ServiceUnavailable);
                authState.SetAuthenticated(
                    changed.Account,
                    changed.AccessToken,
                    AuthSessionAuthority.Supabase);
                authState.SetTransientCredentials(account, NewPassword);
                resultMessage = "Đổi mật khẩu thành công. Tài khoản đã sẵn sàng sử dụng.";
            }
            else
            {
                var result = ApiGuard.Require(await api.PostAsync<
                    ChangePasswordRequest,
                    PasswordChangeResultDto>(
                    "api/v1/auth/change-password",
                    new ChangePasswordRequest(
                        CurrentPassword,
                        NewPassword,
                        ConfirmPassword),
                    cts.Token));
                var current = ApiGuard.Require(await api.GetAsync<CurrentAccountDto>(
                    "api/v1/auth/me",
                    cts.Token));
                var token = authState.AccountAccessToken
                    ?? throw new InvalidOperationException("Phiên đăng nhập không còn access token.");
                authState.SetAuthenticated(
                    current,
                    token,
                    AuthSessionAuthority.LocalServer);
                resultMessage = result.Message;
            }
            CurrentPassword = string.Empty;
            NewPassword = string.Empty;
            ConfirmPassword = string.Empty;
            Status = resultMessage;
            StatusTone = "success";
            await completed();
        }
        catch (Exception ex)
        {
            if (ex is PublicCloudApiException { Code: ErrorCodes.PasswordChangeFailed }
                && authState.IsStudent
                && authState.CurrentAccount is { } student)
            {
                var account = StudentAccountIdentifier(student);
                authState.SetTransientCredentials(account, NewPassword);
            }
            FrontendLogger.Log(ex, "ChangePasswordViewModel");
            Status = ex.Message;
            StatusTone = "danger";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseCommand() =>
        (ChangePasswordCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();

    private static string StudentAccountIdentifier(CurrentAccountDto student) =>
        new[] { student.Email, student.StudentCode, student.Username }
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?.Trim()
        ?? throw new InvalidOperationException("Không xác định được tài khoản học sinh.");
}
