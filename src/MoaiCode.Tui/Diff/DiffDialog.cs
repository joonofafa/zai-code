using System.Text;
using MoaiCode.Core.Diff;
using MoaiCode.Localization;

namespace MoaiCode.Tui.Diff;

/// <summary>
/// /diff 대화상자(Claude Code /diff 이식). 첫 화면은 커밋하지 않은 변경(git diff HEAD), ←/→ 로 이 실행의 턴별 변경
/// (최근 턴부터)으로 옮긴다. ↑/↓ 로 파일을 고르고 Enter 로 그 파일의 diff 를 본다. Esc 는 한 단계 뒤로(목록이면 닫기).
/// 원본은 상세 화면을 스크롤 없이 다 그리지만, 여기는 대체 화면(alternate screen)에 그려 ↑/↓·PgUp/PgDn 으로 스크롤한다 —
/// 닫으면 대화 화면이 그대로 돌아온다.
/// </summary>
internal sealed class DiffDialog
{
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";

    private readonly List<(string Tab, Func<DiffView> Load)> _sources;
    private readonly DiffView?[] _views;
    private int _source;
    private int _selected;
    private bool _detail;
    private int _scroll;

    private DiffDialog(List<(string Tab, Func<DiffView> Load)> sources)
    {
        _sources = sources;
        _views = new DiffView?[sources.Count];
    }

    private static List<(string Tab, Func<DiffView> Load)> Sources(FileChangeJournal journal, string cwd)
    {
        var sources = new List<(string Tab, Func<DiffView> Load)>
        {
            (L10n.Get("diff.tab.current"), () => GitWorkingDiff.Load(cwd)),
        };
        foreach (var turn in journal.Snapshot())
        {
            sources.Add((L10n.Get("diff.tab.turn", turn.Index), () => DiffModel.FromTurn(turn, cwd)));
        }

        return sources;
    }

    /// <summary>대화형으로 연다. 닫힐 때까지 입력을 단독으로 쓴다.</summary>
    public static void Run(FileChangeJournal journal, string cwd) => new DiffDialog(Sources(journal, cwd)).Loop();

    /// <summary>입출력이 터미널이 아닐 때(파이프·헤드리스) 보여 줄 평문 요약.</summary>
    public static string Summary(FileChangeJournal journal, string cwd)
    {
        var sb = new StringBuilder();
        foreach (var (_, load) in Sources(journal, cwd))
        {
            var v = load();
            sb.AppendLine(v.Subtitle is null ? v.Title : $"{v.Title} {v.Subtitle}");
            if (v.Files.Count == 0)
            {
                sb.AppendLine("  " + (v.FilesCount > 0 ? StatsText(v) + " — " : "") + v.Empty);
                continue;
            }

            sb.AppendLine("  " + StatsText(v));
            foreach (var f in v.Files)
            {
                sb.AppendLine($"  {f.Path}  {BadgeText(f)}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private DiffView View => _views[_source] ??= _sources[_source].Load();

    private void Loop()
    {
        Console.Write("\x1b[?1049h\x1b[?25l");   // 대체 화면 + 커서 숨김
        try
        {
            // 열기 전에 쳐 둔 키(/diff 의 Enter 등)가 바로 파일을 열지 않게 버린다.
            while (Input.TerminalInput.Shared?.TryReadEvent(0) is not null)
            {
            }

            using (ConsolePrompt.Begin())
            {
                var size = Size();
                Draw();
                while (true)
                {
                    var key = NextKey(ref size);
                    if (key is null)
                    {
                        Draw();   // 창 크기가 바뀜
                        continue;
                    }

                    if (!Handle(key.Value))
                    {
                        return;
                    }

                    Draw();
                }
            }
        }
        finally
        {
            Console.Write("\x1b[?25h\x1b[?1049l");
        }
    }

    // 키 하나. 공용 리더가 있으면 짧게 폴링하며 창 크기 변화를 본다(변하면 null → 다시 그림).
    private static ConsoleKeyInfo? NextKey(ref (int W, int H) size)
    {
        if (Input.TerminalInput.Shared is not { } ti)
        {
            return Console.ReadKey(intercept: true);
        }

        while (true)
        {
            switch (ti.TryReadEvent(200))
            {
                case Input.KeyEvent k:
                    return k.Key;
                case Input.CancelEvent:
                    return new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, false, false, false);
            }

            var now = Size();
            if (now != size)
            {
                size = now;
                return null;
            }
        }
    }

    /// <summary>키 처리. false 면 닫는다.</summary>
    private bool Handle(ConsoleKeyInfo key)
    {
        var files = View.Files;
        var page = Math.Max(1, Size().H - 5);
        if (_detail)
        {
            switch (key.Key)
            {
                case ConsoleKey.Escape or ConsoleKey.LeftArrow or ConsoleKey.Backspace:
                    _detail = false;
                    break;
                case ConsoleKey.UpArrow:
                    _scroll--;
                    break;
                case ConsoleKey.DownArrow:
                    _scroll++;
                    break;
                case ConsoleKey.PageUp:
                    _scroll -= page;
                    break;
                case ConsoleKey.PageDown or ConsoleKey.Spacebar:
                    _scroll += page;
                    break;
                case ConsoleKey.Home:
                    _scroll = 0;
                    break;
                case ConsoleKey.End:
                    _scroll = int.MaxValue;
                    break;
                default:
                    switch (key.KeyChar)
                    {
                        case 'q':
                            return false;
                        case 'h':
                            _detail = false;
                            break;
                        case 'k':
                            _scroll--;
                            break;
                        case 'j':
                            _scroll++;
                            break;
                        case 'g':
                            _scroll = 0;
                            break;
                        case 'G':
                            _scroll = int.MaxValue;
                            break;
                    }

                    break;
            }

            return true;
        }

        switch (key.Key)
        {
            case ConsoleKey.Escape:
                return false;
            case ConsoleKey.UpArrow:
                _selected = Math.Max(0, _selected - 1);
                break;
            case ConsoleKey.DownArrow:
                _selected = Math.Min(Math.Max(0, files.Count - 1), _selected + 1);
                break;
            case ConsoleKey.LeftArrow:
                MoveSource(-1);
                break;
            case ConsoleKey.RightArrow:
                MoveSource(+1);
                break;
            case ConsoleKey.Enter:
                if (_selected < files.Count)
                {
                    _detail = true;
                    _scroll = 0;
                }

                break;
            default:
                switch (key.KeyChar)
                {
                    case 'q':
                        return false;
                    case 'k':
                        _selected = Math.Max(0, _selected - 1);
                        break;
                    case 'j':
                        _selected = Math.Min(Math.Max(0, files.Count - 1), _selected + 1);
                        break;
                    case 'h':
                        MoveSource(-1);
                        break;
                    case 'l':
                        MoveSource(+1);
                        break;
                }

                break;
        }

        return true;
    }

    private void MoveSource(int delta)
    {
        var next = Math.Clamp(_source + delta, 0, _sources.Count - 1);
        if (next != _source)
        {
            _source = next;
            _selected = 0;   // 원본과 같이 화면을 바꾸면 첫 파일부터
        }
    }

    private void Draw()
    {
        var (w, h) = Size();
        var rows = _detail ? DetailRows(w, h) : ListRows(w, h);
        var sb = new StringBuilder();
        for (var i = 0; i < rows.Count && i < h; i++)
        {
            sb.Append($"\x1b[{i + 1};1H\x1b[2K").Append(rows[i]).Append(Reset);
        }

        if (rows.Count < h)
        {
            sb.Append($"\x1b[{rows.Count + 1};1H\x1b[J");   // 아래 남은 줄 지움(마지막 줄까지 찼으면 지우면 안 된다)
        }
        Console.Write(sb.ToString());
    }

    private List<string> ListRows(int w, int h)
    {
        var v = View;
        var rows = new List<string> { TitleRow(v, w) };
        if (_sources.Count > 1)
        {
            rows.Add(TabsRow(w));
        }

        if (v.FilesCount > 0)
        {
            rows.Add(StatsRow(v));
        }

        rows.Add("");
        var area = Math.Max(1, h - rows.Count - 2);
        if (v.Files.Count == 0)
        {
            rows.Add(Dim(SelectList.Clip(v.Empty, w - 1)));
        }
        else
        {
            var visible = v.Files.Count <= area ? v.Files.Count : Math.Max(1, area - 2);
            var start = Math.Clamp(_selected - visible / 2, 0, Math.Max(0, v.Files.Count - visible));
            var paged = v.Files.Count > visible;
            if (paged)
            {
                rows.Add(start > 0 ? Dim(L10n.Get("diff.more.above", start)) : "");
            }

            for (var i = start; i < start + visible; i++)
            {
                rows.Add(FileRow(v.Files[i], i == _selected, w));
            }

            if (paged)
            {
                var below = v.Files.Count - start - visible;
                rows.Add(below > 0 ? Dim(L10n.Get("diff.more.below", below)) : "");
            }
        }

        while (rows.Count < h - 1)
        {
            rows.Add("");
        }

        rows.Add(Help(L10n.Get(_sources.Count > 1 ? "diff.help.list" : "diff.help.listSingle"), w));
        return rows;
    }

    private List<string> DetailRows(int w, int h)
    {
        var v = View;
        var f = v.Files[Math.Clamp(_selected, 0, v.Files.Count - 1)];
        var head = Bold + SelectList.Clip(f.Path, Math.Max(10, w - 24)) + Reset + "  " + Counts(f);
        if (f.IsNew)
        {
            head += Dim(" " + L10n.Get(f.IsUntracked ? "diff.badge.untracked" : "diff.badge.new"));
        }

        var rows = new List<string> { head, TuiTheme.Fg(TuiTheme.Role.Border) + new string('─', Math.Max(1, w - 1)) };
        var body = Body(f, w);
        var area = Math.Max(1, h - rows.Count - 1);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, body.Count - area));
        rows.AddRange(body.Skip(_scroll).Take(area));
        while (rows.Count < h - 1)
        {
            rows.Add("");
        }

        var help = L10n.Get("diff.help.detail");
        if (body.Count > area)
        {
            help += "  " + L10n.Get("diff.position", _scroll + 1, Math.Min(body.Count, _scroll + area), body.Count);
        }

        rows.Add(Help(help, w));
        return rows;
    }

    private static List<string> Body(DiffFile f, int w)
    {
        var body = new List<string>();
        if (f.IsBinary)
        {
            body.Add(Dim(L10n.Get("diff.file.binary")));
            return body;
        }

        if (f.IsLarge)
        {
            body.Add(Dim(L10n.Get("diff.file.large")));
            return body;
        }

        if (f.Hunks.Count == 0)
        {
            body.Add(Dim(f.IsUntracked ? L10n.Get("diff.file.untracked", f.Path) : L10n.Get("common.noChanges")));
            return body;
        }

        var maxLine = f.Hunks.Max(x => Math.Max(x.OldStart + x.OldLines, x.NewStart + x.NewLines));
        var digits = Math.Max(1, maxLine.ToString().Length);
        var textWidth = Math.Max(10, w - 1 - (digits * 2 + 4));
        foreach (var hunk in f.Hunks)
        {
            body.Add(TuiTheme.Fg(TuiTheme.Role.Info) + $"@@ -{hunk.OldStart},{hunk.OldLines} +{hunk.NewStart},{hunk.NewLines} @@");
            int o = hunk.OldStart, n = hunk.NewStart;
            foreach (var line in hunk.Lines)
            {
                var kind = line[0];
                var text = SelectList.Clip(Printable(line[1..]), textWidth);
                var oldNo = kind == '+' ? "" : (o++).ToString();
                var newNo = kind == '-' ? "" : (n++).ToString();
                var gutter = Dim($"{oldNo.PadLeft(digits)} {newNo.PadLeft(digits)} ");
                body.Add(kind switch
                {
                    '+' => gutter + TuiTheme.Fg(TuiTheme.Role.DiffAdded) + "+ " + text,
                    '-' => gutter + TuiTheme.Fg(TuiTheme.Role.DiffRemoved) + "- " + text,
                    _ => gutter + "  " + text,
                });
            }
        }

        if (f.IsTruncated)
        {
            body.Add(Dim(L10n.Get("diff.file.truncated", DiffModel.MaxLinesPerFile)));
        }

        return body;
    }

    private static string TitleRow(DiffView v, int w)
    {
        var title = SelectList.Clip(v.Title, w - 1);
        var room = w - 2 - LineEditor.DisplayWidth(title);
        return TuiTheme.Fg(TuiTheme.Role.Accent) + Bold + title + Reset
            + (v.Subtitle is not null && room > 3 ? " " + Dim(SelectList.Clip(v.Subtitle, room)) : "");
    }

    private string TabsRow(int w)
    {
        // 화면 폭에 들어가는 만큼 현재 탭 주변만(턴이 많으면 양 끝은 ◀ ▶ 로).
        var labels = _sources.Select(s => s.Tab).ToList();
        int from = _source, to = _source;
        int Width(int a, int b) => Enumerable.Range(a, b - a + 1).Sum(i => LineEditor.DisplayWidth(labels[i]) + 3) + 4;
        while (true)
        {
            var grew = false;
            if (to + 1 < labels.Count && Width(from, to + 1) < w)
            {
                to++;
                grew = true;
            }

            if (from > 0 && Width(from - 1, to) < w)
            {
                from--;
                grew = true;
            }

            if (!grew)
            {
                break;
            }
        }

        var sb = new StringBuilder();
        if (from > 0 || _source > 0)
        {
            sb.Append(Dim("◀ "));
        }

        for (var i = from; i <= to; i++)
        {
            if (i > from)
            {
                sb.Append(Dim(" · "));
            }

            sb.Append(i == _source ? Bold + labels[i] + Reset : Dim(labels[i]));
        }

        if (to < labels.Count - 1 || _source < labels.Count - 1)
        {
            sb.Append(Dim(" ▶"));
        }

        return sb.ToString();
    }

    private static string StatsRow(DiffView v)
    {
        var s = Dim(L10n.Get(v.FilesCount == 1 ? "diff.stats.one" : "diff.stats.many", v.FilesCount));
        if (v.Added > 0)
        {
            s += TuiTheme.Fg(TuiTheme.Role.DiffAdded) + $" +{v.Added}" + Reset;
        }

        if (v.Removed > 0)
        {
            s += TuiTheme.Fg(TuiTheme.Role.DiffRemoved) + $" -{v.Removed}" + Reset;
        }

        return s;
    }

    private static string StatsText(DiffView v)
        => L10n.Get(v.FilesCount == 1 ? "diff.stats.one" : "diff.stats.many", v.FilesCount)
           + (v.Added > 0 ? $" +{v.Added}" : "") + (v.Removed > 0 ? $" -{v.Removed}" : "");

    private static string FileRow(DiffFile f, bool selected, int w)
    {
        var badge = Badge(f);
        var badgeWidth = LineEditor.DisplayWidth(BadgeText(f));
        var pathWidth = Math.Max(10, w - 1 - 2 - badgeWidth - 2);
        var path = SelectList.Clip(f.Path, pathWidth);
        var pad = new string(' ', Math.Max(1, w - 1 - 2 - LineEditor.DisplayWidth(path) - badgeWidth));
        return selected
            ? TuiTheme.Fg(TuiTheme.Role.Selected) + Bold + "❯ " + path + Reset + pad + badge
            : "  " + path + pad + badge;
    }

    // 목록 오른쪽 표시: 줄 수, 또는 바이너리·큰 파일·추적 안 함.
    private static string BadgeText(DiffFile f)
    {
        if (f.IsBinary)
        {
            return L10n.Get("diff.badge.binary");
        }

        if (f.IsLarge)
        {
            return L10n.Get("diff.badge.large");
        }

        if (f.IsUntracked && f.Added == 0)
        {
            return L10n.Get("diff.badge.untracked");
        }

        var parts = new List<string>();
        if (f.Added > 0)
        {
            parts.Add($"+{f.Added}");
        }

        if (f.Removed > 0)
        {
            parts.Add($"-{f.Removed}");
        }

        return string.Join(" ", parts);
    }

    private static string Badge(DiffFile f)
    {
        if (f.IsBinary || f.IsLarge || (f.IsUntracked && f.Added == 0))
        {
            return Dim(BadgeText(f));
        }

        return Counts(f);
    }

    private static string Counts(DiffFile f)
    {
        var s = "";
        if (f.Added > 0)
        {
            s += TuiTheme.Fg(TuiTheme.Role.DiffAdded) + $"+{f.Added}" + Reset;
        }

        if (f.Removed > 0)
        {
            s += (s.Length > 0 ? " " : "") + TuiTheme.Fg(TuiTheme.Role.DiffRemoved) + $"-{f.Removed}" + Reset;
        }

        return s;
    }

    private static string Help(string text, int w) => TuiTheme.Fg(TuiTheme.Role.Help) + SelectList.Clip(text, w - 1);

    private static string Dim(string s) => TuiTheme.Fg(TuiTheme.Role.Dim) + s + Reset;

    // 탭은 4칸, 그 밖의 제어 문자는 보이는 기호로 — 화면 좌표가 어긋나지 않게.
    private static string Printable(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == '\t')
            {
                sb.Append("    ");
            }
            else if (char.IsControl(c))
            {
                sb.Append('·');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static (int W, int H) Size()
    {
        try
        {
            var w = Console.WindowWidth;
            var h = Console.WindowHeight;
            return (w > 10 ? w : 80, h > 5 ? h : 24);
        }
        catch (IOException)
        {
            return (80, 24);
        }
    }
}
