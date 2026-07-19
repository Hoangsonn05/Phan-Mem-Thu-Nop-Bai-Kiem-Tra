using System.Collections.ObjectModel;
using System.Windows.Input;
using ExamTransfer.Desktop.Core;
using ExamTransfer.Desktop.Services;

namespace ExamTransfer.Desktop.ViewModels;

public sealed class DashboardViewModel : ObservableObject, IAsyncInitializable, IDisposable
{
    private readonly IBackendClient api;
    private readonly CancellationTokenSource disposeCts = new();
    private string status = "Đang đồng bộ số liệu";
    private bool isBusy;
    private bool initialized;
    private bool disposed;

    public DashboardViewModel(IBackendClient api)
    {
        this.api = api;
        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !IsBusy);
    }

    public ObservableCollection<MetricCard> Metrics { get; } = new();

    public ObservableCollection<ActivityItem> Activities { get; } = new();

    public ObservableCollection<AlertItem> Alerts { get; } = new();

    public ActiveSessionCard ActiveSession { get; private set; } =
        new("Kiểm tra Lập trình Java", "JAVA-2407", "Đang diễn ra", 36, 31, 28, "00:42:18");

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

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (initialized)
        {
            return Task.CompletedTask;
        }

        initialized = true;
        return LoadAsync(cancellationToken);
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

        IsBusy = true;
        Status = "Đang tải dữ liệu tổng quan";
        try
        {
            var response = await api.GetDashboardAsync();
            cancellationToken.ThrowIfCancellationRequested();

            Metrics.Clear();
            Alerts.Clear();
            Activities.Clear();

            if (response?.Success == true && response.Data is not null)
            {
                var data = response.Data;
                Metrics.Add(new("Lớp học", data.ClassCount.ToString("N0"), "đang quản lý", "\uE716", "primary", "+2 tháng này"));
                Metrics.Add(new("Bài kiểm tra", data.ExamCount.ToString("N0"), "đã tạo", "\uE8A5", "accent", "6 bản nháp"));
                Metrics.Add(new("Phòng đang chạy", data.ActiveSessionCount.ToString("N0"), "phiên trực tiếp", "\uE9D2", "success", "ổn định"));
                Metrics.Add(new("Chưa chấm", data.PendingGradingCount.ToString("N0"), "bài cần xử lý", "\uE70B", "warning", "ưu tiên hôm nay"));

                foreach (var warning in data.Warnings)
                {
                    Alerts.Add(new("Cảnh báo hệ thống", warning, "warning", "\uE7BA"));
                }

                if (data.RecentSessions.FirstOrDefault() is { } session)
                {
                    ActiveSession = new(
                        session.Title,
                        session.RoomCode,
                        session.Status.ToString(),
                        session.Counts.Total,
                        session.Counts.Connected,
                        session.Counts.Submitted,
                        FormatRemaining(session.EffectiveDeadlineUtc, session.ServerNowUtc));
                    Raise(nameof(ActiveSession));
                }

                Status = data.Warnings.Count == 0 ? "Hệ thống vận hành ổn định" : $"Có {data.Warnings.Count} cảnh báo cần xem";
            }
            else
            {
                LoadFallbackMetrics();
                Alerts.Add(new("Dữ liệu tạm thời", response?.Error?.Message ?? "Không lấy được số liệu mới; ứng dụng giữ trạng thái gần nhất để tiếp tục thao tác.", "info", "\uE946"));
                Status = "Đang giữ trạng thái cục bộ";
            }

            Activities.Add(new("21:08", "Học sinh Nguyễn Minh Anh đã nộp bài", "Phòng JAVA-2407 - đúng hạn", "success", "\uE8FB"));
            Activities.Add(new("21:03", "Đã xác minh 3 file đề", "SHA-256 khớp hoàn toàn", "primary", "\uE73E"));
            Activities.Add(new("20:56", "Thiết bị SV-018 mất kết nối", "Đã tự động kết nối lại sau 8 giây", "warning", "\uE968"));
            Activities.Add(new("20:45", "Phòng thi bắt đầu", "36 học sinh được duyệt", "accent", "\uE768"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            FrontendLogger.Log(ex, "DashboardViewModel.LoadAsync");
            LoadFallbackMetrics();
            Alerts.Add(new("Không tải được dữ liệu", ex.Message, "danger", "\uE783"));
            Status = "Không thể đồng bộ tổng quan";
        }
        finally
        {
            if (!disposed)
            {
                IsBusy = false;
            }
        }
    }

    private void LoadFallbackMetrics()
    {
        Metrics.Clear();
        Metrics.Add(new("Lớp học", "12", "đang quản lý", "\uE716", "primary", "+2 tháng này"));
        Metrics.Add(new("Bài kiểm tra", "28", "đã tạo", "\uE8A5", "accent", "6 bản nháp"));
        Metrics.Add(new("Phòng đang chạy", "1", "phiên trực tiếp", "\uE9D2", "success", "ổn định"));
        Metrics.Add(new("Chưa chấm", "24", "bài cần xử lý", "\uE70B", "warning", "ưu tiên hôm nay"));
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
