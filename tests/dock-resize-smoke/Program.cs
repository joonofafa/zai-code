// 스모크: BottomDock 리사이즈 회귀 검증 (grow/shrink/grow 반복).
//
// 구조: 두 모드.
//  --child <out>  : pty(slave=fd0/1) 안에서 실행. 콘솔 크기 폴링으로 리사이즈를 감지해
//                   실제 OnResize/HandleResizeInTurn 을 호출하고, 단계별 ANSI 바이트를
//                   base64로 out 에 기록. (Console.SetOut 으로 캡처 — WindowHeight 는
//                   fd 기반이라 pty 크기 ioctl 에 계속 반응한다)
//                   CPR 프로브(CursorRowProbeForTest)를 SMOKE_MODEL 환경변수(A|B)로 주입해
//                   터미널 성장 모델을 시뮬레이션한다(pty엔 DSR 에 응답하는 에뮬레이터가 없다).
//  --verify <out> : 기록을 재생해 미니 VT100 에뮬레이터로 화면을 재구성, 검증.
//                   SMOKE_MODEL 과 같은 성장 모델로 재생:
//                     ModelA(하단 고정): grow 내용 아래로 평행이동 / shrink 위 행 탈락(하단 보존)
//                     ModelB(상단 고정): grow 내용 제자리+빈 줄 추가 / shrink 아래 행 절단(상단 보존)
//                   — 실제 터미널이 어느 쪽인지 런타임에 알 수 없어 둘 다 통과해야 한다.
//
// 검증 항목(모든 단계): (1) composer 박스 정확히 1개(❯ 1개, 구분선 2행, STATUS 1행)
// (2) 대화 본문 마지막 줄 화면 보존 (3) 누적 공백 검증: 같은 크기로 돌아온 단계의
// 대화↔박스 사이 공백이 기준 단계(install/turn-enter) 대비 +4행 이내인가
// (리사이즈 사이클로 공백이 계속 누적되던 증상의 직접 검증).
using MoaiCode.Tui;
using System.Reflection;
using System.Text;

return args.Length == 2 && args[0] == "--child"
    ? Child.Run(args[1])
    : Verify.Run(args.Length == 2 && args[0] == "--verify" ? args[1] : args[0]);

// ── 자식: pty 안에서 실제 리사이즈 경로 구동 ──────────────────────────────────
public static class Child
{
    public static int Run(string outPath)
    {
        using var file = new StreamWriter(new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        var cap = new StringWriter();
        Console.SetOut(cap);   // 창 크기(Console.WindowHeight)는 fd 기반이라 pty 계속 반영

        var dock = new BottomDock(() => "STATUS-LINE");
        var t = typeof(BottomDock);
        const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;
        var draw = t.GetMethod("Draw", F)!;
        var onResize = t.GetMethod("OnResize", F)!;
        var lastW = t.GetField("_lastW", F)!;
        var lastH = t.GetField("_lastH", F)!;
        var lastCursorRow = t.GetField("_lastCursorRow", F)!;
        var buf = new StringBuilder();

        // CPR 프로브: pty엔 DSR(ESC[6n) 에 응답하는 에뮬레이터가 없어 성장 모델을 시뮬레이션한다.
        // BottomDock 의 기대(직전 커서 행)와 현재 크기 변화량으로, 해당 모델 터미널이 보고할 행을 반환.
        var modelA = (Environment.GetEnvironmentVariable("SMOKE_MODEL") ?? "A") != "B";
        t.GetField("CursorRowProbeForTest", F)!.SetValue(dock,
            new Func<int?>(() =>
            {
                var expect = (int)lastCursorRow.GetValue(dock)!;
                var oldH = (int)lastH.GetValue(dock)!;
                var d = Console.WindowHeight - oldH;
                return modelA ? expect + d : expect;   // A: 평행이동(+d) / B: 제자리
            }));

        string Drain()
        {
            var s = cap.ToString();
            cap.GetStringBuilder().Clear();
            return s;
        }

        void Mark(string name)
        {
            file.WriteLine($"MARK {name}");
            file.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(Drain())));
        }

        void WaitHeight(int h)
        {
            while (Console.WindowHeight != h) Thread.Sleep(10);
        }

        // 프롬프트 경로: 호출부(ReadLine 폴)가 하듯 OnResize 후 _lastH/_lastW 갱신.
        void ResizeStep(int h)
        {
            WaitHeight(h);
            onResize.Invoke(dock, new object[] { buf, 0 });
            lastW.SetValue(dock, 100);
            lastH.SetValue(dock, h);
        }

        // 1) 초기 24행: 대화 본문 12줄 + composer 설치
        WaitHeight(24);
        for (var i = 1; i <= 12; i++)
        {
            Console.Write($"CONV LINE {i,2}\r\n");
        }

        draw.Invoke(dock, new object[] { buf, 0, null });
        lastW.SetValue(dock, 100);
        lastH.SetValue(dock, 24);
        Mark("install");

        // 2) 프롬프트 경로 사이클: 커졌다 작게 다시 크게(공란 누적 증상) + 원복
        ResizeStep(30); Mark("grow30");
        ResizeStep(18); Mark("shrink18");
        ResizeStep(30); Mark("grow30b");
        ResizeStep(40); Mark("grow40");
        ResizeStep(24); Mark("back24");          // ← 누적 검증 기준 단계

        // 3) 턴 중 경로 사이클(상태줄 중복 증상)
        dock.KeepComposerForTurn("typed input");  // echo + 턴모드 진입(공개 API)
        Mark("turn-enter");
        WaitHeight(30);
        dock.HandleResizeInTurn();                // 내부에서 _lastH 갱신
        Mark("turn-grow30");
        WaitHeight(24);
        dock.HandleResizeInTurn();
        Mark("turn-back24");                      // ← 누적 검증 기준 단계

        return 0;
    }
}

// ── 검증: 기록 재생 + 두 성장 모델 시뮬레이션 ────────────────────────────────
public static class Verify
{
    public sealed class Vt
    {
        public readonly int W;
        public int H;
        public char[] Cells;
        int _top, _bot, _cx, _cy;
        (int, int)? _saved;

        public Vt(int w, int h) { W = w; H = h; Cells = new char[w * h]; _bot = h - 1; }

        private void ScrollUpOne()
        {
            for (var r = _top; r < _bot; r++) Array.Copy(Cells, (r + 1) * W, Cells, r * W, W);
            for (var k = 0; k < W; k++) Cells[_bot * W + k] = '\0';
        }

        public void Resize(int newH, bool bottomAnchored)
        {
            var next = new char[W * newH];
            var d = newH - H;
            int dstRow, srcRow0;
            if (d >= 0)
            {
                // grow: A(하단 고정)=내용이 아래로 평행이동 / B(상단 고정)=제자리, 아래 빈 줄 추가
                dstRow = bottomAnchored ? d : 0;
                srcRow0 = 0;
            }
            else
            {
                // shrink: A=위 행 탈락(하단 보존) / B=아래 행 절단(상단 보존)
                dstRow = 0;
                srcRow0 = bottomAnchored ? -d : 0;
            }

            var rows = Math.Min(H, newH);
            for (var r = 0; r < rows; r++)
            {
                var s = srcRow0 + r;
                if (s >= 0 && s < H && dstRow + r < newH)
                    Array.Copy(Cells, s * W, next, (dstRow + r) * W, W);
            }

            Cells = next;
            H = newH;
            _bot = Math.Min(_bot, H - 1);
            _cy = Math.Min(_cy, H - 1);
        }

        public void Feed(string b)
        {
            for (var i = 0; i < b.Length; i++)
            {
                var c = b[i];
                if (c == '\x1b' && i + 1 < b.Length && b[i + 1] == '[')
                {
                    var j = i + 2;
                    while (j < b.Length && !char.IsLetter(b[j]) && b[j] != '?') j++;
                    if (j >= b.Length) break;
                    var fin = b[j];
                    var body = b[(i + 2)..j];
                    var parts = body.Split(';');
                    int P(int k) => k < parts.Length && int.TryParse(parts[k], out var v) ? v : 0;
                    switch (fin)
                    {
                        case 'r':
                            _top = body.Length == 0 || P(0) <= 0 ? 0 : P(0) - 1;
                            _bot = parts.Length > 1 && int.TryParse(parts[1], out var t) && t > 0 ? Math.Min(t - 1, H - 1) : H - 1;
                            _cx = _cy = 0;
                            break;
                        case 'H':
                            _cy = Math.Clamp(P(0) > 0 ? P(0) - 1 : 0, 0, H - 1);
                            _cx = Math.Clamp(P(1) > 0 ? P(1) - 1 : 0, 0, W - 1);
                            break;
                        case 'K':
                            if (P(0) is 0 or 2) for (var k = _cx; k < W; k++) Cells[_cy * W + k] = '\0';
                            break;
                        case 'J':
                            if (P(0) is 0 or 2) for (var r = _cy; r < H; r++) for (var k = 0; k < W; k++) Cells[r * W + k] = '\0';
                            break;
                        case 'L':
                            for (var q = 0; q < Math.Max(1, P(0)); q++)
                            {
                                for (var r = _bot; r > _cy; r--) Array.Copy(Cells, (r - 1) * W, Cells, r * W, W);
                                for (var k = 0; k < W; k++) Cells[_cy * W + k] = '\0';
                            }

                            break;
                        case 'M':
                            for (var q = 0; q < Math.Max(1, P(0)); q++)
                            {
                                for (var r = _cy; r < _bot; r++) Array.Copy(Cells, (r + 1) * W, Cells, r * W, W);
                                for (var k = 0; k < W; k++) Cells[_bot * W + k] = '\0';
                            }

                            break;
                        case 'm': case 'h': case 'l': break;
                    }

                    i = j;
                }
                else if (c == '\r') _cx = 0;
                else if (c == '\n') { if (_cy == _bot) ScrollUpOne(); else if (_cy < H - 1) _cy++; }
                else if (c == '\x1b' && i + 1 < b.Length && b[i + 1] == '7') { _saved = (_cx, _cy); i++; }
                else if (c == '\x1b' && i + 1 < b.Length && b[i + 1] == '8') { if (_saved is { } sv) (_cx, _cy) = sv; i++; }
                else if (c >= ' ')
                {
                    if (_cx >= W) { _cx = 0; if (_cy == _bot) ScrollUpOne(); else if (_cy < H - 1) _cy++; }
                    Cells[_cy * W + _cx++] = c;
                }
            }
        }

        public string Row(int r)
        {
            var sb = new StringBuilder();
            for (var k = 0; k < W; k++)
            {
                var ch = r < 0 || r >= H ? '\0' : Cells[r * W + k];
                sb.Append(ch == '\0' ? ' ' : ch);
            }

            return sb.ToString();
        }
    }

    public static int Run(string path)
    {
        var steps = new List<(string Name, string Bytes)>();
        using (var f = File.OpenText(path))
        {
            string? name;
            string? b64;
            while ((name = f.ReadLine()) is not null && (b64 = f.ReadLine()) is not null)
            {
                steps.Add((name["MARK ".Length..], Encoding.UTF8.GetString(Convert.FromBase64String(b64))));
            }
        }

        // 녹화와 같은 성장 모델로 재생(Child 의 CPR 프로브가 시뮬레이션한 모델).
        var bottomAnchored = (Environment.GetEnvironmentVariable("SMOKE_MODEL") ?? "A") != "B";
        var model = bottomAnchored ? "ModelA(하단고정)" : "ModelB(상단고정)";
        var vt = new Vt(100, 24);
        var errs = new List<string>();
        var baseGap = -1;        // 기준 단계(install)의 대화↔박스 공백
        var turnBaseGap = -1;    // 기준 단계(turn-enter)의 공백

        foreach (var (name, bytes) in steps)
        {
            // 단계 ANSI 는 "새 크기" 기준 절대좌표 — 먹이기 전에 VT 를 먼저 리사이즈.
            var digits = new string(name.Where(char.IsDigit).ToArray());
            if (name != "install" && name != "turn-enter" && digits.Length > 0)
            {
                vt.Resize(int.Parse(digits), bottomAnchored);
            }

            vt.Feed(bytes);
            var gap = MeasureGap(vt, name, errs, turn: name.StartsWith("turn"));
            if (name == "install") baseGap = gap;
            if (name == "turn-enter") turnBaseGap = gap;
            if ((name == "back24" || name == "turn-back24") && gap >= 0)
            {
                var baseline = name == "back24" ? baseGap : turnBaseGap;
                if (baseline >= 0 && gap - baseline > 4)
                {
                    errs.Add($"{name}: 공백 {baseline}→{gap}행 누적 (허용 +4)");
                }
            }
        }

        Console.WriteLine($"[{model}] {(errs.Count == 0 ? "PASS" : "FAIL")}");
        foreach (var e in errs) Console.WriteLine($"    {e}");
        return errs.Count == 0 ? 0 : 1;
    }

    // 구분선 행: 폭 전체가 '─' 로 덮인 행(버블 테두리처럼 짧거나 모서리(╭╮)가 있는 행과 구별).
    private static bool IsSep(string r, int w)
        => r.Count(ch => ch == '─') > w / 2 && r.Trim('─').Trim().Length == 0;

    // 대화 마지막 줄과 첫 구분선 사이의 순수 공백 행 수. 화면이 다 차 평가 불가면 -1.
    private static int MeasureGap(Vt vt, string phase, List<string> errs, bool turn)
    {
        var rows = Enumerable.Range(0, vt.H).Select(vt.Row).ToList();
        void Err(string m) => errs.Add($"{phase}: {m}");

        var prompt = rows.Count(r => r.Contains('❯'));
        var status = rows.Count(r => r.Contains("STATUS-LINE"));
        var sep = rows.Count(r => IsSep(r, vt.W));
        if (prompt != 1) Err($"composer 입력행 {prompt}개 (유령 박스)");
        if (sep != 2) Err($"구분선 {sep}행 (정상 2)");
        if (status != 1) Err($"상태줄 {status}개 (중복/소실)");

        var conv = rows.FindIndex(r => r.Contains("CONV LINE 12"));
        if (conv < 0) Err("대화 마지막 줄(CONV LINE 12) 유실");
        if (turn && rows.All(r => !r.Contains("typed input"))) Err("턴 echo 유실");

        var firstSep = rows.FindIndex(r => IsSep(r, vt.W));
        if (conv < 0 || firstSep < 0 || firstSep <= conv) return -1;
        return Enumerable.Range(conv + 1, firstSep - conv - 1).Count(i => vt.Row(i).Trim().Length == 0);
    }
}
