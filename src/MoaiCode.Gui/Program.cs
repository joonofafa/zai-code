using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using MoaiCode.Config;

namespace MoaiCode.Gui;

internal static class Program
{
    // 단일 인스턴스 가드(세션 스코프). Mutex = 존재 플래그, EventWaitHandle = '기존 창 앞으로' 신호.
    private const string SingleInstanceMutexName = "MoAiDesktop.SingleInstance.v1";
    private const string ShowEventName = "MoAiDesktop.ShowWindow.v1";
    private static Mutex? _instanceMutex; // 프로세스 수명 동안 유지(GC 방지).

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

        // 단일 인스턴스: 이미 실행 중이면 기존 창을 앞으로 올리고 이 인스턴스는 조용히 종료.
        var createdNew = true;
        EventWaitHandle? showEvent = null;
        try
        {
            _instanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        }
        catch (Exception ex)
        {
            // 가드 불가 환경 → 정상 실행(다중 인스턴스 허용은 하되 시작 크래시는 방지).
            MoaiLog.Warn($"single-instance guard unavailable: {ex.GetType().Name}");
            createdNew = true;
        }

        if (!createdNew)
        {
            try
            {
                showEvent?.Set(); // 실행 중인 인스턴스에게 '창 앞으로' 신호
            }
            catch
            {
                // 신호 실패는 무시 — 어차피 이 인스턴스는 종료한다.
            }

            return;
        }

        // 첫(유일) 인스턴스: 다른 인스턴스의 '앞으로' 신호를 대기하는 백그라운드 리스너.
        if (showEvent is not null)
        {
            StartShowListener(showEvent);
        }

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

    // '기존 창 앞으로' 신호를 계속 대기하다가 신호가 오면 메인 창을 포그라운드로.
    private static void StartShowListener(EventWaitHandle showEvent)
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    showEvent.WaitOne();
                    App.BringExistingToFront();
                }
                catch
                {
                    break; // 핸들 dispose 등 → 리스너 종료
                }
            }
        })
        {
            IsBackground = true,
            Name = "single-instance-listener",
        };
        t.Start();
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
