using System;
using System.Threading.Tasks;
using Avalonia;
using MoaiCode.Config;

namespace MoaiCode.Gui;

internal static class Program
{
    // Avalonia 초기화보다 먼저 어떤 코드도 UI 를 만지면 안 된다.
    [STAThread]
    public static void Main(string[] args)
    {
        // Global unhandled-exception hooks -> file log (not shown to user; for admin diagnosis).
        // Ensures a crash on ANY thread still leaves a trace.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            if (e.IsTerminating)
            {
                MoaiLog.Fatal("Unhandled exception, process terminating", ex);
            }
            else
            {
                MoaiLog.Error("Unhandled exception (AppDomain, non-fatal)", ex);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            MoaiLog.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            MoaiLog.Fatal("App start/run failed", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
