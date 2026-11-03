using System.Text;

namespace MoaiCode.Core.Tools;

/// <summary>
/// 권한 판정용 셸 명령 해석 — 실제로 무엇이 실행되는지를 찾는다. rm 판정·프로그램 규칙·deny 규칙·원격 판정이 모두 이 결과를
/// 쓴다(같은 명령을 서로 다르게 읽지 않게). 2026-11-03 두 번째 검토 뒤 따옴표를 이해하는 해석기로 다시 썼다.
/// <list type="bullet">
/// <item>따옴표('…', "…", $'…' ANSI-C 이스케이프), 역슬래시, 줄 이음(\ + 개행), 주석(#)을 셸처럼 푼다.
/// 따옴표 안의 글자는 명령이 아니다 — `git commit -m "fix(shutdown)"` 은 shutdown 을 실행하지 않는다.</item>
/// <item>그러나 실행되는 문자열은 다시 해석한다: $(…)·백틱·&lt;(…) 의 안쪽, sh -c '…', su -c, eval, ssh host …, env -S,
/// watch, script -c, tmux/screen, find -exec … ;, kubectl/oc exec·rsh, docker exec, heredoc·here-string 을 셸에 넣는 경우.</item>
/// <item>명령 위치: VAR=값, 래퍼(sudo·env·timeout·nice·xargs·nohup·systemd-run·nsenter …)와 그 옵션, 예약어({ } ! if then do …)를
/// 건너뛴 다음 단어가 실행되는 프로그램이다.</item>
/// <item>해석할 수 없는 형태는 <see cref="Parsed.Indirect"/> 로 표시한다(실행 프로그램이 변수·치환·글롭에서 옴, eval,
/// 표준 입력을 프로그램으로 읽는 셸·인터프리터, at/batch …). 권한 게이트는 이것을 "모르면 묻는다"로 다룬다.</item>
/// </list>
/// </summary>
public static class ShellCommand
{
    /// <summary>리다이렉션 하나(연산자와 대상). 대상은 따옴표를 푼 값.</summary>
    public sealed record Redirect(string Op, string Target);

    /// <summary>
    /// 해석된 단순 명령 하나. <see cref="Words"/> 는 실행되는 프로그램부터(래퍼 제외), <see cref="Wrappers"/> 는 그 앞의 래퍼 프로그램들.
    /// </summary>
    public sealed record Parsed(
        List<string> Words,
        List<string> Wrappers,
        List<Redirect> Redirects,
        string? Stdin,
        bool FromPipe,
        string? PipedFrom,
        string? Indirect,
        int Depth,
        bool CodeFromString);

    private const int MaxDepth = 6;
    private const int MaxCommands = 4000;

    // ---------------- 공개 API ----------------

    /// <summary>명령 문자열의 모든 단순 명령(안쪽에서 실행되는 것 포함). 해석 한도를 넘으면 Indirect 로 표시된 항목이 들어 있다.</summary>
    public static List<Parsed> Parse(string command)
    {
        command ??= string.Empty;
        // 한 번의 권한 판정이 같은 명령을 여러 번 해석한다(rm 판정·차단·파괴·원격·스크립트) — 최근 결과를 다시 쓴다.
        // 결과 목록은 호출측이 읽기만 한다.
        lock (Cache)
        {
            foreach (var (text, parsed) in Cache)
            {
                if (string.Equals(text, command, StringComparison.Ordinal))
                {
                    return parsed;
                }
            }
        }

        var result = new List<Parsed>();
        ParseInto(command, 0, result, codeFromString: false);
        lock (Cache)
        {
            Cache.Enqueue((command, result));
            while (Cache.Count > 8)
            {
                Cache.Dequeue();
            }
        }

        return result;
    }

    private static readonly Queue<(string Text, List<Parsed> Parsed)> Cache = new();

    /// <summary>프로그램 코드(python -c, 스크립트 파일): 코드 자체 항목 하나("(code)", 코드) + 문자열 리터럴을 셸 명령으로 본 항목들.</summary>
    public static List<Parsed> ParseCode(string code)
    {
        var result = new List<Parsed>();
        AddCode(code ?? string.Empty, -1, result);
        return result;
    }

    /// <summary>각 단순 명령의 실행 단어 목록(프로그램부터).</summary>
    public static IEnumerable<List<string>> Commands(string command) =>
        Parse(command).Where(p => p.Words.Count > 0).Select(p => p.Words);

    /// <summary>경로를 뗀 프로그램 이름(/usr/bin/ssh → ssh).</summary>
    public static string Program(string word)
    {
        var slash = word.LastIndexOfAny(new[] { '/', '\\' });
        return slash < 0 ? word : word[(slash + 1)..];
    }

    /// <summary>"-n" 처럼 값을 갖는 옵션의 값까지 건너뛰며 첫 번째 일반 단어의 위치. 없으면 -1.</summary>
    public static int FirstOperand(IReadOnlyList<string> words, int start, ISet<string> valueFlags)
    {
        for (var i = start; i < words.Count; i++)
        {
            var t = words[i];
            if (t == "--")
            {
                return i + 1 < words.Count ? i + 1 : -1;
            }

            if (t.StartsWith('-') && t.Length > 1)
            {
                if (!t.Contains('=', StringComparison.Ordinal) && valueFlags.Contains(t))
                {
                    i++;
                }

                continue;
            }

            return i;
        }

        return -1;
    }

    /// <summary>절대 경로를 정규화한다(// · . · .. 정리). 상대 경로는 그대로.</summary>
    public static string NormalizePath(string path)
    {
        if (!path.StartsWith('/'))
        {
            return path;
        }

        var parts = new List<string>();
        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".")
            {
                continue;
            }

            if (seg == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(seg);
        }

        return "/" + string.Join('/', parts);
    }

    // ---------------- 어휘 분석 ----------------

    private enum Kind { Word, Op }

    private sealed class Token
    {
        public Kind Kind;
        public string Text = string.Empty;        // 단어: 따옴표를 푼 값, 연산자: 연산자 문자열
        public bool Expanded;                     // $변수·$(…)·백틱이 있었다(따옴표 밖이든 "…" 안이든)
        public bool Globbed;                      // 따옴표 밖의 * ? [
        public bool Quoted;                       // 따옴표가 있었다
        public List<string> Subs = new();         // 안쪽에서 실행되는 $(…)·백틱·<(…) 의 명령 문자열
        public bool ProcSub;                      // <(…) / >(…) 그 자체인 단어
    }

    private static readonly HashSet<string> SafeVars = new(StringComparer.Ordinal) { "HOME", "PWD", "USER", "LOGNAME", "OLDPWD" };

    private static List<Token> Lex(string s, out List<(int TokenIndex, string Delim, bool StripTabs)> heredocs, out Dictionary<int, string> heredocBodies)
    {
        var tokens = new List<Token>();
        heredocs = new();
        heredocBodies = new();
        var pendingHeredocs = new List<(int TokenIndex, string Delim, bool StripTabs)>();
        Token? cur = null;
        var sb = new StringBuilder();

        void EndWord()
        {
            if (cur is not null)
            {
                cur.Text = sb.ToString();
                tokens.Add(cur);
                cur = null;
                sb.Clear();
            }
        }

        Token Cur() => cur ??= new Token { Kind = Kind.Word };

        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];

            // 줄 이음
            if (c == '\\' && i + 1 < s.Length && s[i + 1] == '\n')
            {
                i += 2;
                continue;
            }

            if (c is ' ' or '\t' or '\r')
            {
                EndWord();
                i++;
                continue;
            }

            if (c == '\n')
            {
                EndWord();
                tokens.Add(new Token { Kind = Kind.Op, Text = ";" });
                i++;
                // 대기 중인 heredoc 본문을 읽는다.
                foreach (var (ti, delim, strip) in pendingHeredocs)
                {
                    var body = new StringBuilder();
                    while (i < s.Length)
                    {
                        var nl = s.IndexOf('\n', i);
                        var line = nl < 0 ? s[i..] : s[i..nl];
                        i = nl < 0 ? s.Length : nl + 1;
                        var cmp = strip ? line.TrimStart('\t') : line;
                        if (cmp.TrimEnd('\r') == delim)
                        {
                            break;
                        }

                        body.Append(line).Append('\n');
                    }

                    heredocBodies[ti] = body.ToString();
                }

                pendingHeredocs.Clear();
                continue;
            }

            if (c == '#' && cur is null)
            {
                while (i < s.Length && s[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c is ';' or '&' or '|' or '(' or ')')
            {
                // <( >( 프로세스 치환은 단어의 일부로 아래에서 처리한다.
                EndWord();
                var op = c.ToString();
                if (c is '&' or '|' or ';' && i + 1 < s.Length && (s[i + 1] == c || (c == '|' && s[i + 1] == '&') || (c == '&' && s[i + 1] == '>')))
                {
                    if (c == '&' && s[i + 1] == '>')
                    {
                        // &> / &>> 리다이렉션
                        var redirect = i + 2 < s.Length && s[i + 2] == '>' ? "&>>" : "&>";
                        tokens.Add(new Token { Kind = Kind.Op, Text = redirect });
                        i += redirect.Length;
                        continue;
                    }

                    op = s.Substring(i, 2);
                }

                tokens.Add(new Token { Kind = Kind.Op, Text = op });
                i += op.Length;
                continue;
            }

            if (c is '<' or '>')
            {
                // 프로세스 치환 <(…) >(…)
                if (i + 1 < s.Length && s[i + 1] == '(')
                {
                    var (inner, next) = Balanced(s, i + 1);
                    var t = Cur();
                    t.ProcSub = true;
                    t.Expanded = true;
                    t.Subs.Add(inner);
                    sb.Append(c).Append("(…)");
                    i = next;
                    continue;
                }

                // 앞의 숫자는 fd 번호(2>)
                if (cur is not null && sb.Length > 0 && sb.ToString().All(char.IsAsciiDigit) && !cur.Quoted)
                {
                    cur = null;
                    sb.Clear();
                }
                else
                {
                    EndWord();
                }

                string op;
                if (s.AsSpan(i).StartsWith("<<<"))
                {
                    op = "<<<";
                }
                else if (s.AsSpan(i).StartsWith("<<-"))
                {
                    op = "<<-";
                }
                else if (s.AsSpan(i).StartsWith("<<"))
                {
                    op = "<<";
                }
                else if (s.AsSpan(i).StartsWith(">>"))
                {
                    op = ">>";
                }
                else if (s.AsSpan(i).StartsWith(">|") || s.AsSpan(i).StartsWith(">&") || s.AsSpan(i).StartsWith("<>") || s.AsSpan(i).StartsWith("<&"))
                {
                    op = s.Substring(i, 2);
                }
                else
                {
                    op = c.ToString();
                }

                tokens.Add(new Token { Kind = Kind.Op, Text = op });
                i += op.Length;
                if (op is "<<" or "<<-")
                {
                    // 구분자 단어를 읽고 본문은 다음 개행에서.
                    while (i < s.Length && s[i] is ' ' or '\t')
                    {
                        i++;
                    }

                    var start = i;
                    while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not (';' or '|' or '&' or '<' or '>' or '(' or ')'))
                    {
                        i++;
                    }

                    var delim = s[start..i].Replace("'", "", StringComparison.Ordinal).Replace("\"", "", StringComparison.Ordinal).Replace("\\", "", StringComparison.Ordinal);
                    tokens.Add(new Token { Kind = Kind.Word, Text = delim, Quoted = true });
                    pendingHeredocs.Add((tokens.Count - 2, delim, op == "<<-"));
                }

                continue;
            }

            // 단어 문자
            var w = Cur();
            switch (c)
            {
                case '\'':
                    {
                        w.Quoted = true;
                        var end = s.IndexOf('\'', i + 1);
                        if (end < 0)
                        {
                            end = s.Length;
                        }

                        sb.Append(s, i + 1, end - i - 1);
                        i = Math.Min(s.Length, end + 1);
                        break;
                    }

                case '"':
                    {
                        w.Quoted = true;
                        i++;
                        while (i < s.Length && s[i] != '"')
                        {
                            if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] is '"' or '\\' or '$' or '`' or '\n')
                            {
                                if (s[i + 1] != '\n')
                                {
                                    sb.Append(s[i + 1]);
                                }

                                i += 2;
                                continue;
                            }

                            if (s[i] == '$' || s[i] == '`')
                            {
                                i = Expansion(s, i, w, sb);
                                continue;
                            }

                            sb.Append(s[i]);
                            i++;
                        }

                        i++;
                        break;
                    }

                case '$' when i + 1 < s.Length && s[i + 1] == '\'':
                    {
                        w.Quoted = true;
                        i = AnsiC(s, i + 2, sb);
                        break;
                    }

                case '$' or '`':
                    i = Expansion(s, i, w, sb);
                    break;

                case '\\':
                    if (i + 1 < s.Length)
                    {
                        w.Quoted = true;
                        sb.Append(s[i + 1]);
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }

                    break;

                case '*' or '?' or '[':
                    w.Globbed = true;
                    sb.Append(c);
                    i++;
                    break;

                default:
                    sb.Append(c);
                    i++;
                    break;
            }
        }

        EndWord();
        heredocs = pendingHeredocs;   // 개행 없이 끝난 heredoc(본문 없음)
        return tokens;
    }

    // $VAR / ${VAR} / $(…) / `…` — 단어에 원문 표기를 남기고, 실행되는 부분은 Subs 에 모은다. 다음 위치를 돌려준다.
    private static int Expansion(string s, int i, Token w, StringBuilder sb)
    {
        if (s[i] == '`')
        {
            var end = i + 1;
            while (end < s.Length && s[end] != '`')
            {
                end += s[end] == '\\' ? 2 : 1;
            }

            w.Expanded = true;
            w.Subs.Add(s.Substring(i + 1, Math.Min(end, s.Length) - i - 1));
            sb.Append("$(…)");
            return Math.Min(s.Length, end + 1);
        }

        if (i + 1 < s.Length && s[i + 1] == '(')
        {
            var (inner, next) = Balanced(s, i + 1);
            w.Expanded = true;
            // $(( … )) 산술 확장은 명령이 아니다.
            if (!(inner.StartsWith('(') && inner.EndsWith(')')))
            {
                w.Subs.Add(inner);
            }

            sb.Append("$(…)");
            return next;
        }

        if (i + 1 < s.Length && s[i + 1] == '{')
        {
            var end = s.IndexOf('}', i + 2);
            end = end < 0 ? s.Length : end;
            var name = s[(i + 2)..end];
            if (!SafeVars.Contains(name))
            {
                w.Expanded = true;
            }

            sb.Append('$').Append(name);
            return Math.Min(s.Length, end + 1);
        }

        var j = i + 1;
        while (j < s.Length && (char.IsAsciiLetterOrDigit(s[j]) || s[j] == '_'))
        {
            j++;
        }

        if (j == i + 1 && j < s.Length && s[j] is '@' or '*' or '#' or '?' or '$' or '!' or '-')
        {
            j++;
        }

        var var = s[(i + 1)..j];
        if (var.Length == 0)
        {
            sb.Append('$');
            return i + 1;
        }

        if (!SafeVars.Contains(var))
        {
            w.Expanded = true;
        }

        sb.Append('$').Append(var);
        return j;
    }

    // s[open] == '(' — 짝이 맞는 ')' 까지의 안쪽과 다음 위치. 따옴표 안의 괄호는 세지 않는다.
    private static (string Inner, int Next) Balanced(string s, int open)
    {
        var depth = 0;
        var i = open;
        char quote = '\0';
        for (; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0')
            {
                if (c == '\\' && quote == '"' && i + 1 < s.Length)
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == '\\' && i + 1 < s.Length)
            {
                i++;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return (s.Substring(open + 1, i - open - 1), i + 1);
                }
            }
        }

        return (s[Math.Min(open + 1, s.Length)..], s.Length);   // 닫히지 않음 — 끝까지
    }

    // $'…' 의 ANSI-C 이스케이프(\xHH, \NNN, \n, \t …)를 푼다. 닫는 따옴표 다음 위치를 돌려준다.
    private static int AnsiC(string s, int i, StringBuilder sb)
    {
        while (i < s.Length && s[i] != '\'')
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                var n = s[i + 1];
                switch (n)
                {
                    case 'x':
                        {
                            var j = i + 2;
                            var hex = new StringBuilder();
                            while (j < s.Length && hex.Length < 2 && Uri.IsHexDigit(s[j]))
                            {
                                hex.Append(s[j++]);
                            }

                            if (hex.Length > 0)
                            {
                                sb.Append((char)Convert.ToInt32(hex.ToString(), 16));
                            }

                            i = j;
                            continue;
                        }

                    case >= '0' and <= '7':
                        {
                            var j = i + 1;
                            var oct = new StringBuilder();
                            while (j < s.Length && oct.Length < 3 && s[j] is >= '0' and <= '7')
                            {
                                oct.Append(s[j++]);
                            }

                            sb.Append((char)Convert.ToInt32(oct.ToString(), 8));
                            i = j;
                            continue;
                        }

                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'e' or 'E': sb.Append('\x1b'); break;
                    default: sb.Append(n); break;
                }

                i += 2;
                continue;
            }

            sb.Append(s[i]);
            i++;
        }

        return Math.Min(s.Length, i + 1);
    }

    // ---------------- 구문 → 단순 명령 ----------------

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "{", "}", "!", "if", "then", "else", "elif", "fi", "do", "done", "while", "until", "time", "coproc", "function",
        "esac", "[[", "]]",
    };

    // 이 예약어로 시작하는 단순 명령은 실행이 아니라 목록(for x in a b / case $x in / select).
    private static readonly HashSet<string> HeaderWords = new(StringComparer.Ordinal) { "for", "select", "case" };

    private static void ParseInto(string command, int depth, List<Parsed> result, bool codeFromString)
    {
        if (depth > MaxDepth || result.Count > MaxCommands)
        {
            result.Add(new Parsed(new List<string>(), new List<string>(), new List<Redirect>(), null, false, null, "too-complex", depth, codeFromString));
            return;
        }

        var tokens = Lex(command, out _, out var bodies);
        var words = new List<Token>();
        var redirects = new List<Redirect>();
        string? stdin = null;
        var fromPipe = false;
        string? pipedFrom = null;
        string? lastProgram = null;

        void Flush(bool nextFromPipe)
        {
            if (words.Count > 0 || redirects.Count > 0)
            {
                lastProgram = Emit(words, redirects, stdin, fromPipe, pipedFrom, depth, result, codeFromString);
            }

            words = new List<Token>();
            redirects = new List<Redirect>();
            stdin = null;
            pipedFrom = nextFromPipe ? lastProgram : null;
            fromPipe = nextFromPipe;
        }

        for (var t = 0; t < tokens.Count; t++)
        {
            var tok = tokens[t];
            if (tok.Kind == Kind.Op)
            {
                switch (tok.Text)
                {
                    case "|" or "|&":
                        Flush(nextFromPipe: true);
                        continue;
                    case ";" or ";;" or "&" or "&&" or "||" or "(" or ")":
                        Flush(nextFromPipe: false);
                        continue;
                }

                // 리다이렉션: 다음 단어가 대상.
                var target = t + 1 < tokens.Count && tokens[t + 1].Kind == Kind.Word ? tokens[t + 1] : null;
                if (target is not null)
                {
                    t++;
                    foreach (var sub in target.Subs)
                    {
                        ParseInto(sub, depth + 1, result, codeFromString);
                    }

                    if (tok.Text is "<<" or "<<-")
                    {
                        stdin = (stdin ?? string.Empty) + (bodies.TryGetValue(t - 1, out var body) ? body : string.Empty);
                    }
                    else if (tok.Text == "<<<")
                    {
                        stdin = (stdin ?? string.Empty) + target.Text + "\n";
                    }
                    else
                    {
                        redirects.Add(new Redirect(tok.Text, target.Text));
                    }
                }

                continue;
            }

            words.Add(tok);
        }

        Flush(nextFromPipe: false);
    }

    // 단순 명령 하나를 해석해 결과에 넣고, 실행 프로그램 이름을 돌려준다.
    private static string? Emit(
        List<Token> toks, List<Redirect> redirects, string? stdin, bool fromPipe, string? pipedFrom, int depth,
        List<Parsed> result, bool codeFromString)
    {
        // find -exec 안의 find -exec … 처럼 ParseInto 를 거치지 않는 중첩도 깊이를 센다 — 한도를 넘으면 "모름"(묻는다).
        // (예전엔 수천 겹이면 스택이 넘쳐 프로세스가 죽었다.)
        if (depth > MaxDepth || result.Count > MaxCommands)
        {
            result.Add(new Parsed(new List<string>(), new List<string>(), new List<Redirect>(), null, false, null, "too-complex", depth, codeFromString));
            return null;
        }

        // 단어 안쪽의 명령 치환은 먼저 실행된다 — 따로 해석한다.
        foreach (var tok in toks)
        {
            foreach (var sub in tok.Subs)
            {
                ParseInto(sub, depth + 1, result, codeFromString);
            }
        }

        var i = 0;
        while (i < toks.Count && !toks[i].Quoted && Reserved.Contains(toks[i].Text))
        {
            i++;
        }

        if (i < toks.Count && !toks[i].Quoted && HeaderWords.Contains(toks[i].Text))
        {
            return null;   // for x in …: 목록이지 실행이 아니다(본문은 do 뒤 다른 단순 명령)
        }

        var texts = toks.Select(t => t.Text).ToList();
        var wrappers = new List<string>();
        string? indirect = null;

        i = SkipToCommand(toks, texts, i, wrappers, depth, result, ref indirect, stdin, fromPipe, codeFromString);

        var commandWords = i < texts.Count ? texts.GetRange(i, texts.Count - i) : new List<string>();
        if (i < toks.Count)
        {
            var prog = toks[i];
            // 실행할 프로그램이 변수·치환·글롭에서 온다 — 무엇이 실행될지 알 수 없다.
            if (prog.Expanded || prog.Globbed || prog.ProcSub)
            {
                indirect ??= "variable-program";
            }

            var name = Program(prog.Text);
            if (name.Length > 0 && name.EndsWith("()", StringComparison.Ordinal))
            {
                commandWords = new List<string>();   // f() { … } 의 머리
            }
            else if (IsShell(name) || IsInterpreter(name))
            {
                indirect ??= ProgramFromInput(toks, i, stdin, fromPipe, pipedFrom, depth, result, name);
            }
            else if (name == "alias")
            {
                // alias x='rm -rf /' : 정의된 문자열을 명령으로 본다(expand_aliases 를 켜면 실행된다).
                foreach (var t in toks.Skip(i + 1))
                {
                    var eq = t.Text.IndexOf('=');
                    if (eq > 0)
                    {
                        ParseInto(t.Text[(eq + 1)..], depth + 1, result, codeFromString);
                    }
                }
            }
        }

        result.Add(new Parsed(commandWords, wrappers, redirects, stdin, fromPipe, pipedFrom, indirect, depth, codeFromString));
        return commandWords.Count > 0 ? Program(commandWords[0]) : null;
    }

    private static readonly HashSet<string> Shells = new(StringComparer.Ordinal) { "sh", "bash", "zsh", "dash", "ksh", "ash", "mksh", "fish", "csh", "tcsh" };

    private static bool IsShell(string name) => Shells.Contains(name);

    private static bool IsInterpreter(string name) =>
        name.StartsWith("python", StringComparison.Ordinal) || name is "perl" or "ruby" or "node" or "nodejs" or "php"
            or "pwsh" or "powershell" or "lua" or "deno" or "bun" or "Rscript" or "tclsh" or "awk" or "gawk";

    // 셸·인터프리터가 프로그램을 어디서 읽는가. -c/-e 문자열은 해석한다. 표준 입력(파이프·heredoc)·/dev/stdin·<(…) 이면 Indirect.
    private static string? ProgramFromInput(
        List<Token> toks, int i, string? stdin, bool fromPipe, string? pipedFrom, int depth, List<Parsed> result, string name)
    {
        var shell = IsShell(name);
        for (var k = i + 1; k < toks.Count; k++)
        {
            var t = toks[k];
            var text = t.Text;
            if (text.StartsWith('-') && text.Length > 1 && !t.ProcSub)
            {
                // -c / -lc / -ec (셸), -c / -e / -E (인터프리터): 다음 단어가 실행할 코드.
                var codeFlag = shell
                    ? !text.StartsWith("--", StringComparison.Ordinal) && text.Contains('c')
                    : text is "-c" or "-e" or "-E" or "--eval" or "-r" || (name is "perl" or "ruby" && text.StartsWith('-') && !text.StartsWith("--", StringComparison.Ordinal) && text.Contains('e'));
                if (codeFlag)
                {
                    if (k + 1 < toks.Count)
                    {
                        var code = toks[k + 1];
                        if (shell)
                        {
                            ParseInto(code.Text, depth + 1, result, codeFromString: false);
                        }
                        else
                        {
                            AddCode(code.Text, depth, result);
                        }

                        return code.Expanded ? "variable-program" : null;
                    }

                    return null;
                }

                if (name is "awk" or "gawk" && text == "-f")
                {
                    return null;   // 스크립트 파일 — 게이트가 읽는다
                }

                continue;
            }

            // 첫 번째 일반 인자 = 스크립트 파일(게이트가 읽는다). /dev/stdin·- ·<(…) 는 입력에서 읽는다.
            if (t.ProcSub)
            {
                return "program-from-input";
            }

            if (text is "-" or "/dev/stdin" or "/dev/fd/0" or "/proc/self/fd/0")
            {
                break;
            }

            if (name is "awk" or "gawk")
            {
                AddCode(text, depth, result);   // awk 'BEGIN{system("…")}' — 프로그램 텍스트
            }

            return null;
        }

        // 인자 없이(또는 -s/-) 실행: 프로그램을 표준 입력에서 읽는다.
        if (stdin is not null)
        {
            if (shell)
            {
                ParseInto(stdin, depth + 1, result, codeFromString: false);
                return null;
            }

            AddCode(stdin, depth, result);
            return null;
        }

        return fromPipe ? "program-from-input" : null;   // 파이프로 들어오는 프로그램은 내용을 모른다
    }

    // 인터프리터 코드: 문자열 리터럴을 셸 명령으로도 본다(os.system("rm -rf /data")). 코드 자체는 CodeFromString 표시로 넘긴다.
    private static void AddCode(string code, int depth, List<Parsed> result)
    {
        result.Add(new Parsed(new List<string> { "(code)", code }, new List<string>(), new List<Redirect>(), null, false, null, null, depth + 1, true));
        foreach (var literal in StringLiterals(code))
        {
            ParseInto(literal, depth + 1, result, codeFromString: true);
        }
    }

    private static IEnumerable<string> StringLiterals(string code)
    {
        for (var i = 0; i < code.Length; i++)
        {
            var q = code[i];
            if (q is not ('"' or '\''))
            {
                continue;
            }

            var sb = new StringBuilder();
            var j = i + 1;
            for (; j < code.Length && code[j] != q; j++)
            {
                if (code[j] == '\\' && j + 1 < code.Length)
                {
                    j++;
                }

                sb.Append(code[j]);
            }

            if (sb.Length > 1)
            {
                yield return sb.ToString();
            }

            i = j;
        }
    }

    // ---------------- 명령 위치 ----------------

    // 인자로 명령을 받는 래퍼와, 값을 하나 받는 옵션.
    private static readonly Dictionary<string, HashSet<string>> Wrappers = new(StringComparer.Ordinal)
    {
        ["sudo"] = Set("-u", "-g", "-h", "-p", "-C", "-D", "-r", "-t", "-U", "-T", "--user", "--group", "--host", "--prompt", "--chdir"),
        ["doas"] = Set("-u", "-C"),
        ["pkexec"] = Set("--user"),
        ["command"] = Set(),
        ["builtin"] = Set(),
        ["exec"] = Set("-a"),
        ["nohup"] = Set(),
        ["time"] = Set("-f", "-o", "--format", "--output"),
        ["nice"] = Set("-n", "--adjustment"),
        ["ionice"] = Set("-c", "-n", "-p", "--class", "--classdata"),
        ["chrt"] = Set(),
        ["taskset"] = Set(),
        ["env"] = Set("-u", "-C", "--unset", "--chdir"),
        ["xargs"] = Set("-I", "-n", "-P", "-L", "-d", "-a", "-E", "-s", "-R", "--max-args", "--max-procs", "--delimiter", "--arg-file", "--replace"),
        ["stdbuf"] = Set("-i", "-o", "-e"),
        ["setsid"] = Set(),
        ["unbuffer"] = Set(),
        ["strace"] = Set("-o", "-p", "-e", "-s", "-u", "-E"),
        ["ltrace"] = Set("-o", "-p", "-e", "-s", "-u"),
        ["runuser"] = Set("-u", "-g", "-G", "--user", "--group"),
        ["timeout"] = Set("-s", "-k", "--signal", "--kill-after"),
        ["chroot"] = Set("--userspec", "--groups"),
        ["flock"] = Set("-w", "-E", "--timeout", "--conflict-exit-code"),
        ["nsenter"] = Set("-t", "--target", "-S", "-G", "--setuid", "--setgid", "-w", "-r"),
        ["unshare"] = Set(),
        ["firejail"] = Set(),
        ["systemd-run"] = Set("-p", "--property", "-u", "--unit", "-E", "--setenv", "-M", "--machine", "--description", "--slice", "--uid", "--gid", "--working-directory"),
        ["sg"] = Set(),
        ["busybox"] = Set(),
        ["toybox"] = Set(),
        ["k3s"] = Set(),
        ["microk8s"] = Set(),
    };

    // 다음 일반 단어 하나(기간·디렉터리·잠금 파일·우선순위·그룹)를 먹고 나서 명령이 오는 래퍼.
    private static readonly HashSet<string> WrappersWithOperand = new(StringComparer.Ordinal) { "timeout", "chroot", "flock", "chrt", "taskset", "sg" };

    private static HashSet<string> Set(params string[] xs) => new(xs, StringComparer.Ordinal);

    private static readonly HashSet<string> SshValueFlags = Set(
        "-p", "-i", "-c", "-o", "-F", "-l", "-b", "-D", "-E", "-e", "-I", "-J", "-L", "-m", "-O", "-R", "-S", "-W", "-w", "-B", "-Q");

    private static readonly HashSet<string> ContainerExecValueFlags = Set("-e", "-u", "-w", "--env", "--user", "--workdir", "--env-file", "--detach-keys");

    private static readonly HashSet<string> KubeExecValueFlags = Set("-c", "--container", "-n", "--namespace", "--context", "--kubeconfig", "-f", "--filename", "--pod-running-timeout");

    private static bool IsAssignment(string w)
    {
        var eq = w.IndexOf('=');
        if (eq <= 0)
        {
            return false;
        }

        for (var i = 0; i < eq; i++)
        {
            var c = w[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_') || (i == 0 && char.IsAsciiDigit(c)))
            {
                return false;
            }
        }

        return true;
    }

    // 명령 위치까지 건너뛴다. 문자열을 받아 실행하는 래퍼는 그 문자열을 따로 해석해 result 에 넣는다.
    private static int SkipToCommand(
        List<Token> toks, List<string> w, int i, List<string> wrappers, int depth, List<Parsed> result, ref string? indirect,
        string? stdin, bool fromPipe, bool codeFromString)
    {
        while (i < w.Count)
        {
            if (!toks[i].Quoted && Reserved.Contains(w[i]))
            {
                i++;
                continue;
            }

            if (IsAssignment(w[i]))
            {
                i++;
                continue;
            }

            var prog = Program(w[i]);

            if (prog == "env")
            {
                // env -S '…' / --split-string='…' : 문자열을 명령으로 쪼갠다.
                for (var k = i + 1; k < w.Count && w[k].StartsWith('-'); k++)
                {
                    if (w[k] is "-S" or "--split-string" && k + 1 < w.Count)
                    {
                        wrappers.Add(prog);
                        ParseInto(w[k + 1], depth + 1, result, codeFromString);
                        return w.Count;
                    }

                    if (w[k].StartsWith("--split-string=", StringComparison.Ordinal) || (w[k].StartsWith("-S", StringComparison.Ordinal) && w[k].Length > 2))
                    {
                        wrappers.Add(prog);
                        ParseInto(w[k].StartsWith("-S", StringComparison.Ordinal) ? w[k][2..] : w[k][(w[k].IndexOf('=') + 1)..], depth + 1, result, codeFromString);
                        return w.Count;
                    }
                }
            }

            if (Wrappers.TryGetValue(prog, out var valueFlags))
            {
                wrappers.Add(prog);
                i++;
                while (i < w.Count && w[i].StartsWith('-') && w[i].Length > 1)
                {
                    var flag = w[i];
                    i++;
                    if (flag == "--")
                    {
                        break;
                    }

                    if (!flag.Contains('=', StringComparison.Ordinal) && valueFlags.Contains(flag) && i < w.Count)
                    {
                        i++;
                    }
                }

                if (WrappersWithOperand.Contains(prog) && i < w.Count)
                {
                    i++;
                }

                // busybox/toybox 의 다음 단어는 애플릿(rm 등) — 그대로 명령 위치. k3s/microk8s kubectl 도 같다.
                continue;
            }

            switch (prog)
            {
                case "eval":
                    wrappers.Add(prog);
                    indirect ??= "eval";
                    ParseInto(string.Join(' ', w.Skip(i + 1)), depth + 1, result, codeFromString);
                    return w.Count;

                case "su" or "runuser" or "script":
                    {
                        // su -c '…', runuser -c '…', script -c '…'
                        var c = w.FindIndex(i + 1, t => t is "-c" or "--command");
                        if (c > 0 && c + 1 < w.Count)
                        {
                            wrappers.Add(prog);
                            ParseInto(w[c + 1], depth + 1, result, codeFromString);
                            return w.Count;
                        }

                        if (prog == "su")
                        {
                            return i;
                        }

                        break;
                    }

                case "watch":
                    {
                        wrappers.Add(prog);
                        var at = FirstOperand(w, i + 1, Set("-n", "--interval", "-d"));
                        if (at > 0)
                        {
                            ParseInto(string.Join(' ', w.Skip(at)), depth + 1, result, codeFromString);
                        }

                        return w.Count;
                    }

                case "at" or "batch":
                    wrappers.Add(prog);
                    if (stdin is not null)
                    {
                        ParseInto(stdin, depth + 1, result, codeFromString);
                    }
                    else
                    {
                        indirect ??= "deferred";   // 파이프로 받은 작업 — 내용을 모른다
                    }

                    return w.Count;

                case "tmux":
                    {
                        // tmux new[-session] [-d] [-s name] 'cmd' / send-keys … 'cmd'
                        var verb = w.Skip(i + 1).FirstOrDefault(t => !t.StartsWith('-'));
                        if (verb is "new" or "new-session" or "new-window" or "neww" or "split-window" or "splitw" or "send-keys" or "send" or "respawn-pane" or "run-shell" or "run")
                        {
                            wrappers.Add(prog);
                            indirect ??= "deferred";
                            foreach (var part in w.Skip(i + 2).Where(t => !t.StartsWith('-')))
                            {
                                ParseInto(part, depth + 1, result, codeFromString);
                            }

                            return w.Count;
                        }

                        return i;
                    }

                case "screen":
                    wrappers.Add(prog);
                    indirect ??= "deferred";
                    ParseInto(string.Join(' ', w.Skip(i + 1).SkipWhile(t => t.StartsWith('-'))), depth + 1, result, codeFromString);
                    return w.Count;

                case "parallel":
                    {
                        wrappers.Add(prog);
                        var end = w.IndexOf(":::", i + 1);
                        var cmd = w.Skip(i + 1).Take((end < 0 ? w.Count : end) - i - 1).SkipWhile(t => t.StartsWith('-'));
                        ParseInto(string.Join(' ', cmd), depth + 1, result, codeFromString);
                        return w.Count;
                    }

                case "ssh" or "mosh":
                    {
                        var host = FirstOperand(w, i + 1, SshValueFlags);
                        if (host > 0 && host + 1 < w.Count)
                        {
                            wrappers.Add(prog);
                            // ssh 는 남은 단어를 공백으로 이어 원격 셸에 넘긴다.
                            ParseInto(string.Join(' ', w.Skip(host + 1)), depth + 1, result, codeFromString);
                            return w.Count;
                        }

                        if (stdin is not null)
                        {
                            ParseInto(stdin, depth + 1, result, codeFromString);   // ssh host <<EOF … EOF
                        }

                        return i;
                    }

                case "docker" or "podman" or "nerdctl" when i + 1 < w.Count && w[i + 1] == "exec":
                    {
                        var ctr = FirstOperand(w, i + 2, ContainerExecValueFlags);
                        if (ctr > 0 && ctr + 1 < w.Count)
                        {
                            wrappers.Add(prog);
                            i = ctr + 1;
                            continue;
                        }

                        return i;
                    }

                case "kubectl" or "oc":
                    {
                        // kubectl exec [옵션] pod [--] cmd / oc rsh [옵션] pod cmd / oc exec pod -- cmd / debug … -- cmd
                        var verbAt = FirstOperand(w, i + 1, KubeExecValueFlags);
                        if (verbAt > 0 && w[verbAt] is "exec" or "rsh" or "debug" or "run" or "attach")
                        {
                            var dd = w.IndexOf("--", verbAt + 1);
                            if (dd > 0 && dd + 1 < w.Count)
                            {
                                wrappers.Add(prog);
                                i = dd + 1;
                                continue;
                            }

                            if (w[verbAt] is "exec" or "rsh")
                            {
                                var pod = FirstOperand(w, verbAt + 1, KubeExecValueFlags);
                                if (pod > 0 && pod + 1 < w.Count)
                                {
                                    wrappers.Add(prog);
                                    i = pod + 1;
                                    continue;
                                }
                            }
                        }

                        return i;
                    }

                case "find":
                    {
                        // find … -exec cmd {} ; / -execdir / -ok / -okdir : 그 명령을 따로 해석한다(find 자체는 그대로 명령).
                        for (var k = i + 1; k < w.Count; k++)
                        {
                            if (w[k] is "-exec" or "-execdir" or "-ok" or "-okdir")
                            {
                                var end = k + 1;
                                while (end < w.Count && w[end] is not (";" or "+" or "\\;"))
                                {
                                    end++;
                                }

                                var sub = new List<Token>();
                                for (var x = k + 1; x < end; x++)
                                {
                                    sub.Add(toks[x]);
                                }

                                if (sub.Count > 0)
                                {
                                    Emit(sub, new List<Redirect>(), null, false, null, depth + 1, result, codeFromString);
                                }

                                k = end;
                            }
                        }

                        return i;
                    }
            }

            return i;
        }

        return i;
    }
}
