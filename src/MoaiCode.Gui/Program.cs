using Avalonia;

namespace MoaiCode.Gui;

internal static class Program
{
    // Avalonia 초기화보다 먼저 어떤 코드도 UI 를 만지면 안 된다.
    [STAThread]
    public static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
