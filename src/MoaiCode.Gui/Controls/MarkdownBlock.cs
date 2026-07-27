using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MoaiCode.Gui.Controls;

/// <summary>
/// Dependency-free markdown renderer. Replaces Markdown.Avalonia, which is
/// incompatible with Avalonia 11.2 (crashes in Measure with an unsupported
/// StaticBinding IBinding implementation). Supports headings, bold, italic,
/// inline code, fenced code blocks, bullet/numbered lists, and paragraphs.
/// </summary>
public sealed class MarkdownBlock : Border
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownBlock, string?>(nameof(Markdown));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,Menlo,monospace");
    private static readonly IBrush CodeBg = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
    private static readonly IBrush CodeFg = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x75));

    private static readonly Regex InlineToken = new(
        @"(`[^`]+`|\*\*[^*]+\*\*|__[^_]+__|\*[^*]+\*|_[^_]+_)", RegexOptions.Compiled);

    private static readonly Regex NumberedItem = new(@"^(\d+)\.\s+(.*)$", RegexOptions.Compiled);

    private readonly StackPanel _root = new() { Spacing = 6 };

    public MarkdownBlock()
    {
        Child = _root;
        // 스트리밍으로 답변 높이가 커질 때(SizeChanged) 조상 ScrollViewer 가 이미 하단 근처면
        // 끝으로 따라 스크롤한다(채팅 표준 near-bottom auto-scroll). 사용자가 위로 올려 읽는 중이면
        // 방해하지 않는다 — 마지막 답변이 입력창에 가리던 문제의 근본 대응.
        // SizeChanged 시점엔 조상 ScrollViewer 의 Extent 가 아직 갱신 전일 수 있어, 다음 프레임에 처리.
        SizeChanged += (_, _) => Dispatcher.UIThread.Post(FollowIfNearBottom, DispatcherPriority.Background);
    }

    private void FollowIfNearBottom()
    {
        var sv = this.FindAncestorOfType<ScrollViewer>();
        if (sv is null)
        {
            return;
        }

        // 하단에서 120px 이내이면 '따라가는 중'으로 보고 끝으로. Offset 을 직접 최대로 설정(ScrollToEnd 보정).
        if (sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - 120)
        {
            sv.ScrollToEnd();
            sv.Offset = new Vector(sv.Offset.X, sv.Extent.Height);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == MarkdownProperty)
        {
            Rebuild(Markdown ?? string.Empty);
        }
    }

    private void Rebuild(string md)
    {
        _root.Children.Clear();
        var lines = md.Replace("\r\n", "\n").Split('\n');
        var para = new List<string>();
        var i = 0;

        void FlushPara()
        {
            if (para.Count == 0)
            {
                return;
            }

            _root.Children.Add(Paragraph(string.Join(" ", para)));
            para.Clear();
        }

        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // Fenced code block.
            if (trimmed.StartsWith("```"))
            {
                FlushPara();
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                {
                    code.Add(lines[i]);
                    i++;
                }

                i++; // skip closing fence
                _root.Children.Add(CodeBlock(string.Join("\n", code)));
                continue;
            }

            var heading = HeadingLevel(trimmed);
            if (heading > 0)
            {
                FlushPara();
                _root.Children.Add(Heading(trimmed[(heading + 1)..].Trim(), heading));
                i++;
                continue;
            }

            if (IsBullet(trimmed) || NumberedItem.IsMatch(trimmed))
            {
                FlushPara();
                _root.Children.Add(ListItem(trimmed));
                i++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushPara();
                i++;
                continue;
            }

            para.Add(line);
            i++;
        }

        FlushPara();
    }

    private static int HeadingLevel(string s)
    {
        var n = 0;
        while (n < s.Length && s[n] == '#')
        {
            n++;
        }

        return n is >= 1 and <= 6 && n < s.Length && s[n] == ' ' ? n : 0;
    }

    private static bool IsBullet(string s) =>
        s.StartsWith("- ") || s.StartsWith("* ") || s.StartsWith("+ ");

    private static SelectableTextBlock NewText()
    {
        var tb = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        tb.Inlines ??= new InlineCollection();
        return tb;
    }

    private static Control Heading(string text, int level)
    {
        var tb = NewText();
        tb.FontSize = level switch { 1 => 20, 2 => 17, 3 => 15, _ => 14 };
        tb.FontWeight = FontWeight.SemiBold;
        tb.Margin = new Thickness(0, level <= 2 ? 4 : 2, 0, 0);
        AddInlines(tb.Inlines!, text);
        return tb;
    }

    private static Control CodeBlock(string code)
    {
        var tb = new SelectableTextBlock
        {
            Text = code,
            FontFamily = Mono,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
        };
        return new Border
        {
            Background = CodeBg,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9),
            Child = tb,
        };
    }

    private static Control ListItem(string s)
    {
        string bullet, content;
        var m = NumberedItem.Match(s);
        if (m.Success)
        {
            bullet = m.Groups[1].Value + ".  ";
            content = m.Groups[2].Value;
        }
        else
        {
            bullet = "•  ";
            content = s[2..];
        }

        var tb = NewText();
        tb.FontSize = 14;
        tb.Margin = new Thickness(8, 0, 0, 0);
        tb.Inlines!.Add(new Run(bullet));
        AddInlines(tb.Inlines!, content);
        return tb;
    }

    private static Control Paragraph(string text)
    {
        var tb = NewText();
        tb.FontSize = 14;
        tb.LineHeight = 20;
        AddInlines(tb.Inlines!, text);
        return tb;
    }

    private static void AddInlines(InlineCollection inlines, string text)
    {
        var last = 0;
        foreach (Match m in InlineToken.Matches(text))
        {
            if (m.Index > last)
            {
                inlines.Add(new Run(text[last..m.Index]));
            }

            var t = m.Value;
            if (t[0] == '`')
            {
                inlines.Add(new Run(t[1..^1]) { FontFamily = Mono, Foreground = CodeFg });
            }
            else if (t.StartsWith("**") || t.StartsWith("__"))
            {
                inlines.Add(new Run(t[2..^2]) { FontWeight = FontWeight.Bold });
            }
            else
            {
                inlines.Add(new Run(t[1..^1]) { FontStyle = FontStyle.Italic });
            }

            last = m.Index + m.Length;
        }

        if (last < text.Length)
        {
            inlines.Add(new Run(text[last..]));
        }
    }
}
