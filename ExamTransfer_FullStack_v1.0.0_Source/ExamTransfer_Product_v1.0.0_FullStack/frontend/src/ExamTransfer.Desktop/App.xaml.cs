using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ExamTransfer.Desktop.Core;

namespace ExamTransfer.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(e);
        ViewModels.AppServices.SubmissionRecovery.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ViewModels.AppServices.SubmissionRecovery.Dispose();
        ViewModels.AppServices.PublicRealtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        FrontendLogger.Log(e.Exception, "Application.DispatcherUnhandledException");
        FrontendLogger.ShowDebugDialog(e.Exception, "Application.DispatcherUnhandledException");
        // A view or binding error must not terminate an active exam session.
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            FrontendLogger.Log(exception, "AppDomain.CurrentDomain.UnhandledException");
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        FrontendLogger.Log(e.Exception, "TaskScheduler.UnobservedTaskException");
        e.SetObserved();
    }
}
