using System.Reflection;
using System.Text;
using Spectre.Console;

namespace MoaiCode.Tui;

/// <summary>
/// 시그니처 배너. FIGlet(slant) "MoAI Code"에 가로 그라데이션(인디고→블루→틸)을
/// 문자 단위 truecolor로 입힘. 세 색 모두 어두운 채도라 라이트/다크 양쪽에서 가독.
/// </summary>
public static class Banner
{
    private static readonly string[] Art =
    {
        "███╗   ███╗ ██████╗  █████╗ ██╗     ██████╗ ██████╗ ██████╗ ███████╗",
        "████╗ ████║██╔═══██╗██╔══██╗██║    ██╔════╝██╔═══██╗██╔══██╗██╔════╝",
        "██╔████╔██║██║   ██║███████║██║    ██║     ██║   ██║██║  ██║█████╗  ",
        "██║╚██╔╝██║██║   ██║██╔══██║██║    ██║     ██║   ██║██║  ██║██╔══╝  ",
        "██║ ╚═╝ ██║╚██████╔╝██║  ██║██║    ╚██████╗╚██████╔╝██████╔╝███████╗",
        "╚═╝     ╚═╝ ╚═════╝ ╚═╝  ╚═╝╚═╝     ╚═════╝ ╚═════╝ ╚═════╝ ╚══════╝",
    };

    // 그라데이션 정지점 (RGB): 인디고 → 블루 → 틸
    private static readonly (int R, int G, int B)[] Stops =
    {
        (0x4F, 0x46, 0xE5),
        (0x25, 0x63, 0xEB),
        (0x06, 0x94, 0xB2),
    };

    public static void Render()
    {
        var width = Art.Max(l => l.Length);

        foreach (var line in Art)
        {
            var sb = new StringBuilder();
            for (var x = 0; x < line.Length; x++)
            {
                var c = line[x];
                if (c == ' ')
                {
                    sb.Append(' ');
                    continue;
                }

                var t = width <= 1 ? 0.0 : (double)x / (width - 1);
                var (r, g, b) = Sample(t);
                sb.Append($"[#{r:x2}{g:x2}{b:x2}]{Markup.Escape(c.ToString())}[/]");
            }

            AnsiConsole.MarkupLine(sb.ToString());
        }
    }

    private static (int R, int G, int B) Sample(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        var span = 1.0 / (Stops.Length - 1);
        var i = Math.Min((int)(t / span), Stops.Length - 2);
        var local = (t - i * span) / span;

        var a = Stops[i];
        var b = Stops[i + 1];
        return (
            Lerp(a.R, b.R, local),
            Lerp(a.G, b.G, local),
            Lerp(a.B, b.B, local));
    }

    private static int Lerp(int a, int b, double t) => (int)Math.Round(a + (b - a) * t);

    /// <summary>버전 + 바이너리 빌드시각 (구버전 실행 여부를 한눈에 판별).</summary>
    public static string VersionString()
    {
        var v = Assembly.GetEntryAssembly()?.GetName().Version;
        var ver = v is null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";

        var built = string.Empty;
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                built = " · build " + File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
            }
        }
        catch
        {
            // 빌드시각 표기 실패는 무시 (버전만 표시).
        }

        return $"v{ver}{built}";
    }
}
