// 스모크: BottomDock.Draw 가 그린 ANSI 바이트를 미니 VT100 에뮬레이터로 렌더해
// [구분선 / 입력행(❯) / 구분선 / 상태줄] 레이아웃 검증 — 입력행이 구분선에 덮이는 회귀 탐지.
using MoaiCode.Tui;
using System.Text;

var dock = new BottomDock(new Func<string>(() => "STATUS-LINE"));

var origOut = Console.Out;
var captured = new StringBuilder();
try
{
    Console.SetOut(new StringWriter(captured));
    var draw = typeof(BottomDock).GetMethod("Draw", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    var buf = new StringBuilder();
    draw.Invoke(dock, new object[] { buf, 0, null });      // 최초 설치
    captured.Clear();
    draw.Invoke(dock, new object[] { buf, 0, null });      // 재그리기(설치 후 steady-state)
}
finally { Console.SetOut(origOut); }

// ── 미니 VT100: DECSTBM/CUP/EL/CR/LF/DECSC-DECRC/SGR. Draw 는 절대좌표만 쓰므로 wrap 생략. ──
const int W = 60, H = 24;
var cells = new char[W * H];
int top = 0, bot = H - 1, cx = 0, cy = 0;
(int, int)? saved = null;
var bytes = captured.ToString();
for (var i = 0; i < bytes.Length; i++)
{
    var c = bytes[i];
    if (c == '\x1b' && i + 1 < bytes.Length && bytes[i + 1] == '[')
    {
        var j = i + 2;
        while (j < bytes.Length && !char.IsLetter(bytes[j])) j++;
        if (j >= bytes.Length) break;
        var final = bytes[j];
        var body = bytes[(i + 2)..j];
        var parts = body.Split(';');
        int P(int k) => k < parts.Length && int.TryParse(parts[k], out var v) ? v : 0;
        switch (final)
        {
            case 'r':
                top = body.Length == 0 ? 0 : P(0) - 1;
                bot = parts.Length > 1 && int.TryParse(parts[1], out var b) ? b - 1 : H - 1;
                cx = cy = 0;   // DECSTBM 은 커서를 홈으로
                break;
            case 'H':
                cy = Math.Clamp(P(0) is var r && r > 0 ? r - 1 : 0, 0, H - 1);
                cx = Math.Clamp(P(1) > 0 ? P(1) - 1 : 0, 0, W - 1);
                break;
            case 'K':
                if (P(0) is 0 or 2)
                    for (var k = cx; k < W; k++) cells[cy * W + k] = '\0';
                break;
            case 'J':
                if (P(0) == 2) Array.Clear(cells);
                break;
            case 'm': break;   // SGR 무시
            case 'h' or 'l': break;
        }
        i = j;
    }
    else if (c == '\r') cx = 0;
    else if (c == '\n')
    {
        cy++;
        if (cy > bot)
        {
            cy = bot;
            for (var row = top; row < bot; row++) Array.Copy(cells, (row + 1) * W, cells, row * W, W);
            Array.Clear(cells, bot * W, W);
        }
        if (cy >= H) cy = H - 1;
    }
    else if (c == '\u001b' && i + 1 < bytes.Length && bytes[i + 1] == '7') { saved = (cx, cy); i++; }
    else if (c == '\u001b' && i + 1 < bytes.Length && bytes[i + 1] == '8' && saved.HasValue) { (cx, cy) = saved.Value; i++; }
    else if (c >= ' ')
    {
        if (cx < W) cells[cy * W + cx++] = c;
    }
}

string Row(int r) { var sb = new StringBuilder(); for (var k = 0; k < W; k++) sb.Append(cells[r * W + k] == '\0' ? ' ' : cells[r * W + k]); return sb.ToString(); }

Console.WriteLine($"[captured {bytes.Length} bytes]  terminal {W}x{H}");
Console.WriteLine("--- screen (last 6 rows) ---");
for (var r = H - 6; r < H; r++) Console.WriteLine($"{r + 1,3}|{Row(r)}|");

// ── 검증 ──
var statusIdx = Enumerable.Range(0, H).FirstOrDefault(r => Row(r).Contains("STATUS-LINE"), -1);
var promptIdx = Enumerable.Range(0, H).FirstOrDefault(r => Row(r).Contains("❯"), -1);
var sepBelow = statusIdx >= 0 && Row(statusIdx - 1).Contains('─') ? statusIdx - 1 : -1;
var sepAbove = promptIdx >= 0 && Row(promptIdx - 1).Contains('─') ? promptIdx - 1 : -1;
Console.WriteLine($"\nsepAbove={sepAbove + 1} prompt={promptIdx + 1} sepBelow={sepBelow + 1} status={statusIdx + 1}");
var ok = promptIdx >= 0 && sepAbove == promptIdx - 1 && sepBelow == promptIdx + 1 && statusIdx == promptIdx + 2;
Console.WriteLine(ok ? "PASS: [sep / ❯ input / sep / status] layout intact — 입력행이 구분선에 덮이지 않음"
                    : "FAIL: layout broken — 입력행이 구분선에 덮혔거나 순서 깨짐");
return ok ? 0 : 1;
