using System.Windows.Input;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Shared.Contracts;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class SettingsPageViewModel : ProductPageBase
{
    private readonly IBackendClient api;
    private string connectionStatus = "Đang kiểm tra";
    private string storageSummary = "Chưa có dữ liệu";
    private string applicationVersion = "Không xác định";
    private string synchronizationStatus = "Chưa kiểm tra";
    private string warningSummary = "Không có cảnh báo";
    private bool canSynchronize;

    public SettingsPageViewModel(IBackendClient api)
    {
        this.api = api;
        RefreshCommand = new AsyncRelayCommand(
            () => LoadAsync(DisposeToken),
            () => !IsBusy);
        SyncCommand = new AsyncRelayCommand(
            SyncAsync,
            () => !IsBusy && canSynchronize);
        OpenSupportFolderCommand = new RelayCommand(OpenSupportFolder);
    }

    public string ConnectionStatus { get => connectionStatus; private set => Set(ref connectionStatus, value); }
    public string StorageSummary { get => storageSummary; private set => Set(ref storageSummary, value); }
    public string ApplicationVersion { get => applicationVersion; private set => Set(ref applicationVersion, value); }
    public string SynchronizationStatus { get => synchronizationStatus; private set => Set(ref synchronizationStatus, value); }
    public string WarningSummary { get => warningSummary; private set => Set(ref warningSummary, value); }
    public ICommand RefreshCommand { get; }
    public ICommand SyncCommand { get; }
    public ICommand OpenSupportFolderCommand { get; }

    protected override Task LoadAsync(CancellationToken ct) => RunAsync(
        "Đang cập nhật trạng thái",
        "Trạng thái hệ thống đã được cập nhật",
        async token =>
        {
            var systemTask = api.GetSystemStatusAsync(token);
            var cloudTask = api.GetCloudStatusAsync(token);
            await Task.WhenAll(systemTask, cloudTask);
            var system = ApiGuard.Require(await systemTask);
            var cloud = ApiGuard.Require(await cloudTask);
            ConnectionStatus = system.Ready ? "Đã kết nối" : "Kết nối cần chú ý";
            StorageSummary = $"Còn trống {FormatBytes(system.DiskFreeBytes)}";
            ApplicationVersion = system.Version;
            WarningSummary = system.Warnings.Count == 0
                ? "Không có cảnh báo"
                : string.Join(Environment.NewLine, system.Warnings);
            ApplyCloudStatus(cloud);
        });

    private Task SyncAsync() => RunAsync(
        "Đang yêu cầu đồng bộ",
        "Yêu cầu đồng bộ đã được tiếp nhận",
        async ct =>
        {
            _ = ApiGuard.Require(await api.PostAsync<object, object>(
                "api/v1/cloud/sync",
                new { },
                ct));
            ApplyCloudStatus(ApiGuard.Require(await api.GetCloudStatusAsync(ct)));
        });

    private void ApplyCloudStatus(CloudSyncStatusDto cloud)
    {
        canSynchronize = cloud.Enabled && cloud.Configured && cloud.CanSynchronize;
        SynchronizationStatus = !cloud.Enabled
            ? "Không bật"
            : !cloud.Configured
                ? "Chưa được quản trị viên cấu hình"
                : cloud.LastError is { Length: > 0 }
                    ? $"Cần chú ý · {cloud.PendingItems} mục đang chờ"
                    : $"Hoạt động · {cloud.PendingItems} mục đang chờ";
        RaiseCommands();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var display = (double)Math.Max(0, bytes);
        var unit = 0;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }
        return $"{display:0.#} {units[unit]}";
    }

    private static void OpenSupportFolder()
    {
        Directory.CreateDirectory(FrontendLogger.LogDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            FrontendLogger.LogDirectory)
        {
            UseShellExecute = true
        });
    }

    protected override void RaiseCommands()
    {
        (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SyncCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}
