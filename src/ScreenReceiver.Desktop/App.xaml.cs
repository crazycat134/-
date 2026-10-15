using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace ScreenReceiver.Desktop;

public partial class App : Application
{
    public App()
    {
        this.DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Console.WriteLine($"[FATAL] UI: {e.Exception}");
        System.Windows.MessageBox.Show($"发生错误:\n{e.Exception.Message}\n\n{e.Exception.StackTrace}", "错误");
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            Console.WriteLine($"[FATAL] Domain: {ex}");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Console.WriteLine($"[FATAL] Task: {e.Exception}");
        e.SetObserved();
    }
}
