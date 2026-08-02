using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Spectre.Console;
using Spectre.Console.Rendering;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdTableCell = Markdig.Extensions.Tables.TableCell;

namespace MoaiCode.Tui;

/// <summary>
/// 마크다운 → Spectre.Console IRenderable 변환 (Markdig AST 기반).
/// 헤딩/문단/리스트/코드블록/인용/표/구분선 + 인라인(굵게/기울임/인라인코드/링크) 지원.
/// Spectre는 마크다운을 네이티브로 렌더하지 못하므로 직접 매핑한다.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public static IRenderable Render(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new Markup("[grey70]…[/]");
        }

        MarkdownDocument doc;
        try
        {
            doc = Markdown.Parse(markdown, Pipeline);
        }
        catch
        {
            return new Markup(Markup.Escape(markdown)); // 파싱 실패 시 평문
        }

        var blocks = new List<IRenderable>();
        foreach (var block in doc)
        {
            var r = RenderBlock(block);
            if (r is not null)
            {
                blocks.Add(r);
            }
        }

        return blocks.Count == 0 ? new Markup(Markup.Escape(markdown)) : new Rows(blocks);
    }

    private static IRenderable? RenderBlock(Block block)
    {
        switch (block)
        {
            case HeadingBlock h:
                // 헤딩: 기호(#) 없이 굵게 — 레벨 1~2는 aqua, 그 외는 흰색 굵게.
                var color = h.Level <= 2 ? "aqua" : "white";
                return new Markup($"[bold {color}]{RenderInlines(h.Inline)}[/]");

            case ParagraphBlock p:
                return new Markup(RenderInlines(p.Inline));

            case ListBlock list:
                return RenderList(list, 0);

            case QuoteBlock quote:
                var inner = new List<IRenderable>();
                foreach (var child in quote)
                {
                    var r = RenderBlock(child);
                    if (r is not null)
                    {
                        inner.Add(r);
                    }
                }

                return new Panel(new Rows(inner))
                {
                    Border = BoxBorder.None,
                    Padding = new Padding(2, 0, 0, 0),
                };

            case FencedCodeBlock fenced:
                return RenderCode(fenced.Lines.ToString(), fenced.Info);

            case CodeBlock code:
                return RenderCode(code.Lines.ToString(), null);

            case ThematicBreakBlock:
                return new Rule { Style = Style.Parse("grey") };

            case MdTable table:
                return RenderTable(table);

            default:
                return null;
        }
    }

    private static IRenderable RenderList(ListBlock list, int depth)
    {
        var rows = new List<IRenderable>();
        var index = 1;
        var pad = new string(' ', depth * 2);
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var bullet = list.IsOrdered ? $"{index}." : "•";
            var first = true;
            foreach (var child in item)
            {
                if (child is ListBlock nested)
                {
                    rows.Add(RenderList(nested, depth + 1));
                    continue;
                }

                var inlineMarkup = child is LeafBlock leaf && leaf.Inline is not null
                    ? RenderInlines(leaf.Inline)
                    : "";
                rows.Add(new Markup(first
                    ? $"{pad}[grey70]{bullet}[/] {inlineMarkup}"
                    : $"{pad}  {inlineMarkup}"));
                first = false;
            }

            index++;
        }

        return new Rows(rows);
    }

    private static IRenderable RenderCode(string code, string? info)
    {
        var body = new Markup($"[grey85]{Markup.Escape(code.TrimEnd('\n'))}[/]");
        var panel = new Panel(body)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = Style.Parse("grey39"),
        };
        if (!string.IsNullOrWhiteSpace(info))
        {
            panel.Header = new PanelHeader($"[grey70]{Markup.Escape(info.Trim())}[/]");
        }

        return panel;
    }

    private static IRenderable RenderTable(MdTable mdTable)
    {
        var table = new Spectre.Console.Table { Border = TableBorder.Rounded };
        var headerDone = false;

        foreach (var row in mdTable.OfType<MdTableRow>())
        {
            var cells = row.OfType<MdTableCell>()
                .Select(c => RenderCellText(c))
                .ToArray();

            if (!headerDone)
            {
                foreach (var c in cells)
                {
                    table.AddColumn(new TableColumn($"[bold]{c}[/]"));
                }

                headerDone = true;
            }
            else
            {
                // 컬럼 수 정합
                while (table.Columns.Count < cells.Length)
                {
                    table.AddColumn(new TableColumn(""));
                }

                var padded = cells.Length < table.Columns.Count
                    ? cells.Concat(Enumerable.Repeat("", table.Columns.Count - cells.Length)).ToArray()
                    : cells;
                table.AddRow(padded.Select(x => new Markup(x)).ToArray());
            }
        }

        return headerDone ? table : new Markup("");
    }

    private static string RenderCellText(MdTableCell cell)
    {
        var sb = new StringBuilder();
        foreach (var b in cell)
        {
            if (b is LeafBlock { Inline: not null } leaf)
            {
                sb.Append(RenderInlines(leaf.Inline));
            }
        }

        return sb.ToString();
    }

    private static string RenderInlines(ContainerInline? container)
    {
        if (container is null)
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (var inline in container)
        {
            AppendInline(sb, inline);
        }

        return BoldFallback(sb.ToString());
    }

    // CommonMark 의 right-flanking 규칙상, 닫는 ** 앞이 문장부호이고 뒤가 공백/부호가 아니면 강조로
    // 닫히지 않는다. 한국어는 따옴표 뒤에 조사를 공백 없이 붙여서(예: **"인용"**까지) 이 규칙에 자주
    // 걸리고, 그러면 사용자 화면에 ** 가 그대로 노출된다. 파싱이 놓친 쌍만 여기서 굵게 처리한다.
    // (조립된 문자열 기준 — 실패한 구분자는 여러 LiteralInline 으로 쪼개져 오므로 개별 리터럴로는 못 잡는다.)
    private static readonly System.Text.RegularExpressions.Regex BoldFallbackRe =
        new(@"\*\*(?=\S)(.+?)(?<=\S)\*\*", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string BoldFallback(string s)
        => s.Contains("**", StringComparison.Ordinal)
            ? BoldFallbackRe.Replace(s, m => "[bold]" + m.Groups[1].Value + "[/]")
            : s;

    private static void AppendInline(StringBuilder sb, Inline inline)
    {
        switch (inline)
        {
            case LiteralInline lit:
                sb.Append(Markup.Escape(lit.Content.ToString()));
                break;

            case EmphasisInline em:
                var tag = em.DelimiterCount >= 2 ? "bold" : "italic";
                sb.Append('[').Append(tag).Append(']');
                foreach (var child in em)
                {
                    AppendInline(sb, child);
                }

                sb.Append("[/]");
                break;

            case CodeInline code:
                sb.Append("[deepskyblue1]").Append(Markup.Escape(code.Content)).Append("[/]");
                break;

            case LinkInline link:
                var text = new StringBuilder();
                foreach (var child in link)
                {
                    AppendInline(text, child);
                }

                var label = text.Length > 0 ? text.ToString() : Markup.Escape(link.Url ?? "");
                if (!string.IsNullOrEmpty(link.Url))
                {
                    sb.Append("[link=").Append(Markup.Escape(link.Url)).Append(']').Append(label).Append("[/]");
                }
                else
                {
                    sb.Append(label);
                }

                break;

            case LineBreakInline:
                sb.Append('\n');
                break;

            case AutolinkInline auto:
                sb.Append(Markup.Escape(auto.Url ?? ""));
                break;

            case HtmlEntityInline entity:
                sb.Append(Markup.Escape(entity.Transcoded.ToString()));
                break;

            case HtmlInline html:
                // 원문 HTML 토큰(예: <br>, <div>) — 타입명이 아니라 실제 텍스트를 그대로 출력.
                sb.Append(Markup.Escape(html.Tag ?? ""));
                break;

            case ContainerInline container:
                foreach (var child in container)
                {
                    AppendInline(sb, child);
                }

                break;

            default:
                // 미지원 인라인은 자식이 있으면 재귀, 없으면 무시 (절대 ToString()으로 타입명을 찍지 않음).
                if (inline is ContainerInline c)
                {
                    foreach (var child in c)
                    {
                        AppendInline(sb, child);
                    }
                }

                break;
        }
    }
}
