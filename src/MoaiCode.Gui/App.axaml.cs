using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using MoaiCode.Gui.Sync;
using MoaiCode.Gui.ViewModels;
using MoaiCode.Gui.Views;
using MoaiCode.Localization;

namespace MoaiCode.Gui;

public partial class App : Application
{
    private Window? _mainWindow;
    private TrayIcon? _tray;
    private FolderSyncService? _sync;
    private bool _exiting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var startupSettings = MoaiCode.Config.SettingsLoader.Load(System.IO.Directory.GetCurrentDirectory());
            L10n.SetLanguage(startupSettings.Language);

            // 파일 로그 레벨 적용(settings.json의 logLevel, 기본 info) 후 시작 기록.
            MoaiCode.Config.MoaiLog.Configure(startupSettings.LogLevel);
            MoaiCode.Config.MoaiLog.Info($"MoAI Desktop started (logLevel={MoaiCode.Config.MoaiLog.MinLevel})");

            // 트레이 상주 — 창을 닫아도 프로세스가 종료되지 않는다(백그라운드 폴더 동기화 유지).
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // 저장된 사내 프록시 적용 (어떤 HttpClient 사용보다 먼저).
            MoaiCode.Config.ProxyConfig.Apply(System.IO.Directory.GetCurrentDirectory());

            // 테마(기본 다크) 적용. 로그인·설정은 모두 메인 창 내부에서 전환(별도 창 없음).
            var envTheme = Environment.GetEnvironmentVariable("MOAI_GUI_THEME");
            ApplyTheme(string.IsNullOrWhiteSpace(envTheme) ? GuiSettings.Load().Theme : envTheme);
            LaunchMain(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void LaunchMain(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // 백그라운드 동기화 — 설정에 저장된 연결 폴더를 감시.
        _sync ??= new FolderSyncService();
        _sync.Start(GuiSettings.Load().ConnectedFolders);

        _mainWindow = new MainWindow { DataContext = new MainViewModel(), Icon = LoadIcon() };
        _mainWindow.Closing += OnMainClosing;
        desktop.MainWindow = _mainWindow;

        SetupTray(desktop);
        _mainWindow.Show();
    }

    // 창 닫기 = 종료가 아니라 트레이로 숨김('종료' 메뉴로만 진짜 종료).
    private void OnMainClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            _mainWindow?.Hide();
        }
    }

    private void SetupTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_tray is not null)
        {
            return;
        }

        try
        {
            var open = new NativeMenuItem(L10n.Get("gui.tray.open"));
            open.Click += (_, _) => ShowMain();

            var status = new NativeMenuItem(L10n.Get("gui.tray.syncIdle")) { IsEnabled = false };
            if (_sync is not null)
            {
                // 동기화 상태는 백그라운드 스레드에서 오므로 UI 스레드로 마샬링.
                _sync.Status += msg => Avalonia.Threading.Dispatcher.UIThread.Post(() => status.Header = L10n.Get("gui.tray.syncFmt", msg));
            }

            var quit = new NativeMenuItem(L10n.Get("gui.tray.quit"));
            quit.Click += (_, _) =>
            {
                _exiting = true;
                _sync?.Dispose();
                _tray?.Dispose();
                desktop.Shutdown();
            };

            var menu = new NativeMenu();
            menu.Add(open);
            menu.Add(status);
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(quit);

            _tray = new TrayIcon { ToolTipText = "MoAI Desktop", Menu = menu, Icon = LoadIcon() };
            _tray.Clicked += (_, _) => ShowMain();
            TrayIcon.SetIcons(this, new TrayIcons { _tray });
        }
        catch
        {
            // 트레이 미지원 환경(헤드리스 등) — 무시. 창은 정상 동작.
        }
    }

    /// <summary>다른 인스턴스가 실행됐을 때 기존 창을 앞으로 올린다(단일 인스턴스). UI 스레드로 마샬링.</summary>
    public static void BringExistingToFront() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => (Current as App)?.ShowMain());

    private void ShowMain()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
        // 포그라운드로 강제(다른 창 위로) — Topmost 토글로 Z-순서 끌어올림.
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
    }

    private static WindowIcon? LoadIcon()
    {
        try
        {
            return new WindowIcon(AssetLoader.Open(new Uri("avares://MoaiCode.Gui/Assets/tray.ico")));
        }
        catch
        {
            return null;
        }
    }

    private void ApplyTheme(string theme) =>
        RequestedThemeVariant = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase)
            ? ThemeVariant.Light
            : ThemeVariant.Dark;
}
