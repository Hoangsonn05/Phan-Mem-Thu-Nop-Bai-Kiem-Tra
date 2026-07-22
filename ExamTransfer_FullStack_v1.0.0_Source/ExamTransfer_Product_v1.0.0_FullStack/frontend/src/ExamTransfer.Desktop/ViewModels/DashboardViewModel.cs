using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Services;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class DashboardViewModel : ObservableObject, IAsyncInitializable, IDisposable
{
    private readonly IBackendClient api;
    private readonly CancellationTokenSource disposeCts = new();
    private string status = "Chưa có dữ liệu tổng quan";
    private bool isBusy;
    private bool initialized;
    private bool hasSuccessfulLoad;
    private bool disposed;

    public DashboardViewModel(IBackendClient api)
    {
        this.api = api;
        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !IsBusy);
        ShowEmptyMetrics();
    }

    public ObservableCollection<MetricCard> Metrics { get; } = new();

    public ObservableCollection<ActivityItem> Activities { get; } = new();

    public ObservableCollection<AlertItem> Alerts { get; } = new();

    public ActiveSessionCard? ActiveSession { get; private set; }

    public bool HasActiveSession => ActiveSession is not null;

    public bool HasActivities => Activities.Count > 0;

    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (Set(ref isBusy, value) && RefreshCommand is AsyncRelayCommand command)
            {
                command.RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand RefreshCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (initialized || disposed)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disposeCts.Token);
        try
        {
            await LoadAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            initialized = true;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        disposed = true;
        disposeCts.Cancel();
        disposeCts.Dispose();
    }

    private Task LoadAsync() => LoadAsync(disposeCts.Token);

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (disposed)
        {
            return;
        }

        await RunOnUiAsync(() =>
        {
            IsBusy = true;
            Status = "Đang tải dữ liệu tổng quan";
        });
        try
        {
            var response = await api.GetDashboardAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (response?.Success == true && response.Data is not null)
            {
                await RunOnUiAsync(() => ApplyDashboard(response.Data));
            }
            else
            {
                var message = response?.Error?.Message ?? "Máy chủ không trả về dữ liệu tổng quan hợp lệ.";
                await RunOnUiAsync(() => ApplyLoadFailure(message));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var traceId = FrontendLogger.Log(ex, "DashboardViewModel.LoadAsync");
            await RunOnUiAsync(() => ApplyLoadFailure($"{ex.Message} Mã tra cứu: {traceId}."));
        }
        finally
        {
            if (!disposed)
            {
                await RunOnUiAsync(() => IsBusy = false);
            }
        }
    }

    private void ApplyDashboard(ExamTransfer.Shared.Contracts.DashboardSummaryDto data)
    {
        Metrics.Clear();
        Metrics.Add(new("Lớp học", data.ClassCount.ToString("N0"), "đang hoạt động", "\uE716", "primary", "Dữ liệu từ máy chủ"));
        Metrics.Add(new("Bài kiểm tra", data.ExamCount.ToString("N0"), "chưa lưu trữ", "\uE8A5", "accent", "Dữ liệu từ máy chủ"));
        Metrics.Add(new("Phòng đang chạy", data.ActiveSessionCount.ToString("N0"), "phiên hoạt động", "\uE9D2", "success", "Dữ liệu từ máy chủ"));
        Metrics.Add(new("Chưa chấm", data.PendingGradingCount.ToString("N0"), "bài cần xử lý", "\uE70B", "warning", "Dữ liệu từ máy chủ"));

        Alerts.Clear();
        foreach (var warning in data.Warnings)
        {
            Alerts.Add(new("Cảnh báo hệ thống", warning, "warning", "\uE7BA"));
        }

        Activities.Clear();
        Raise(nameof(HasActivities));

        ActiveSession = data.RecentSessions.FirstOrDefault() is { } session
            ? new ActiveSessionCard(
                session.Title,
                session.RoomCode,
                session.Status.ToString(),
                session.Counts.Total,
                session.Counts.Connected,
                session.Counts.Submitted,
                FormatRemaining(session.EffectiveDeadlineUtc, session.ServerNowUtc))
            : null;
        Raise(nameof(ActiveSession));
        Raise(nameof(HasActiveSession));

        hasSuccessfulLoad = true;
        Status = data.Warnings.Count == 0
            ? "Đã đồng bộ dữ liệu thật từ máy chủ"
            : $"Đã đồng bộ; có {data.Warnings.Count} cảnh báo cần xem";
    }

    private void ApplyLoadFailure(string message)
    {
        foreach (var existing in Alerts.Where(x => x.Title == "Không thể làm mới dữ liệu").ToList())
        {
            Alerts.Remove(existing);
        }

        if (!hasSuccessfulLoad)
        {
            ShowEmptyMetrics();
            ActiveSession = null;
            Activities.Clear();
            Alerts.Clear();
            Raise(nameof(ActiveSession));
            Raise(nameof(HasActiveSession));
            Raise(nameof(HasActivities));
        }

        Alerts.Add(new("Không thể làm mới dữ liệu", message, "danger", "\uE783"));
        Status = hasSuccessfulLoad
            ? "Mất kết nối; đang giữ dữ liệu thật tải thành công gần nhất"
            : "Không có dữ liệu tổng quan vì máy chủ chưa phản hồi";
    }

    private void ShowEmptyMetrics()
    {
        Metrics.Clear();
        Metrics.Add(new("Lớp học", "--", "chưa có dữ liệu", "\uE716", "primary", "Chờ máy chủ phản hồi"));
        Metrics.Add(new("Bài kiểm tra", "--", "chưa có dữ liệu", "\uE8A5", "accent", "Chờ máy chủ phản hồi"));
        Metrics.Add(new("Phòng đang chạy", "--", "chưa có dữ liệu", "\uE9D2", "success", "Chờ máy chủ phản hồi"));
        Metrics.Add(new("Chưa chấm", "--", "chưa có dữ liệu", "\uE70B", "warning", "Chờ máy chủ phản hồi"));
    }

    private static Task RunOnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    private static string FormatRemaining(DateTimeOffset? deadline, DateTimeOffset now)
    {
        if (deadline is null)
        {
            return "--:--:--";
        }

        var remaining = deadline.Value - now;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        return $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
    }
}

public sealed record MetricCard(string Title, string Value, string Subtitle, string Glyph, string Tone, string Trend);
public sealed record ActivityItem(string Time, string Title, string Description, string Tone, string Glyph);
public sealed record AlertItem(string Title, string Description, string Tone, string Glyph);
public sealed record ActiveSessionCard(string Title, string RoomCode, string Status, int Total, int Connected, int Submitted, string TimeLeft)
{
    public double ConnectedPercent => Total <= 0 ? 0 : Connected * 100d / Total;
    public double SubmittedPercent => Total <= 0 ? 0 : Submitted * 100d / Total;
}
