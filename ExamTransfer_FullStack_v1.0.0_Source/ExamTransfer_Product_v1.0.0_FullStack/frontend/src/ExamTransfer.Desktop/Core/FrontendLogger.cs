using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

namespace ExamTransfer.Desktop.Core;

public static class FrontendLogger
{
    private static readonly object Sync = new();

    public static string LogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExamTransfer", "logs");

    public static string LogPath { get; } = Path.Combine(LogDirectory, "frontend.log");

    public static string CurrentMode { get; private set; } = "None";

    public static string CurrentPageKey { get; private set; } = "Welcome";

    public static void SetContext(string? mode = null, string? pageKey = null)
    {
        if (!string.IsNullOrWhiteSpace(mode))
        {
            CurrentMode = mode;
        }

        if (!string.IsNullOrWhiteSpace(pageKey))
        {
            CurrentPageKey = pageKey;
        }
    }

    public static string Log(Exception exception, string source, string? traceId = null)
    {
        traceId ??= Guid.NewGuid().ToString("N");
        var builder = new StringBuilder()
            .AppendLine("------------------------------------------------------------")
            .AppendLine($"timestamp_utc: {DateTimeOffset.UtcNow:O}")
            .AppendLine($"trace_id: {traceId}")
            .AppendLine($"source: {source}")
            .AppendLine($"mode: {CurrentMode}")
            .AppendLine($"page_key: {CurrentPageKey}")
            .AppendLine($"exception_type: {exception.GetType().FullName}")
            .AppendLine($"message: {exception.Message}")
            .AppendLine("stack_trace:")
            .AppendLine(exception.ToString());

        if (exception.InnerException is not null)
        {
            builder.AppendLine("inner_exception:")
                .AppendLine(exception.InnerException.ToString());
        }

        lock (Sync)
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(LogPath, builder.ToString(), Encoding.UTF8);
        }

        return traceId;
    }


    public static void LogMessage(string message, string source)
    {
        var line = $"{DateTimeOffset.UtcNow:O} [{source}] {message}{Environment.NewLine}";
        lock (Sync)
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(LogPath, line, Encoding.UTF8);
        }
    }

    public static void ShowDebugDialog(Exception exception, string source)
    {
        if (!Debugger.IsAttached)
        {
            return;
        }

        MessageBox.Show(exception.ToString(), $"ExamTransfer debug exception: {source}", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

public static class TaskExtensions
{
    public static async void SafeFireAndForget(this Task task, string source)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            FrontendLogger.Log(ex, source);
            FrontendLogger.ShowDebugDialog(ex, source);
        }
    }
}

public interface IAsyncInitializable
{
    Task InitializeAsync(CancellationToken cancellationToken);
}
