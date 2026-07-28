using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ExamTransfer.Desktop.Tests;

internal static class WpfTestHost
{
    private static readonly object Gate = new();
    private static Dispatcher? dispatcher;

    public static void Run(Action action)
    {
        EnsureStarted();
        Exception? failure = null;
        dispatcher!.Invoke(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void EnsureStarted()
    {
        if (dispatcher is not null) return;
        lock (Gate)
        {
            if (dispatcher is not null) return;
            using var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                var application = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                application.Resources["BooleanToVisibilityConverter"] =
                    new BooleanToVisibilityConverter();
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(
                        "pack://application:,,,/ExamTransfer.Desktop;component/Themes/Palette.Light.xaml",
                        UriKind.Absolute)
                });
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(
                        "pack://application:,,,/ExamTransfer.Desktop;component/Themes/Theme.xaml",
                        UriKind.Absolute)
                });
                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "ExamTransfer.WpfTests"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!ready.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("WPF test host did not start.");
        }
    }
}
