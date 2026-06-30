using MoaiCode.Tui;
using Spectre.Console;
using Spectre.Console.Rendering;
using Xunit;

namespace MoaiCode.Core.Tests;

public class MarkdownRendererTests
{
    private static string RenderToText(string md)
    {
        var sw = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(sw),
        });
        console.Write(MarkdownRenderer.Render(md));
        return sw.ToString();
    }

    [Fact]
    public void Html_inline_does_not_leak_type_name()
    {
        // "List<string>" 의 <string>, 그리고 <br> 는 Markdig 가 HtmlInline 으로 파싱한다.
        // 예전엔 inline.ToString() 폴백이 "Markdig.Syntax.Inlines.HtmlInline" 타입명을 찍었다.
        var text = RenderToText("Use List<string> and <br> tags here.");
        Assert.DoesNotContain("Markdig", text);
        Assert.DoesNotContain("HtmlInline", text);
        Assert.Contains("List", text);
    }

    [Fact]
    public void Ordered_list_renumbers_sequentially()
    {
        // 모델이 흔히 모든 항목을 "1."로 쓴다. 렌더는 1,2,3으로 다시 매겨야 한다.
        var text = RenderToText("1. first\n1. second\n1. third");
        Assert.Contains("1.", text);
        Assert.Contains("2.", text);
        Assert.Contains("3.", text);
    }

    [Fact]
    public void Renders_rich_markdown_without_throwing()
    {
        const string md = """
            # Heading

            Some **bold** and *italic* and `code` and a [link](https://x).

            - item one
            - item two

            ```python
            def hello():
                print("hi")
            ```

            | A | B |
            |---|---|
            | 1 | 2 |

            > a quote
            """;

        var r = MarkdownRenderer.Render(md);
        Assert.NotNull(r);
        Assert.IsAssignableFrom<IRenderable>(r);
    }

    [Fact]
    public void Empty_input_is_safe()
    {
        Assert.NotNull(MarkdownRenderer.Render(""));
        Assert.NotNull(MarkdownRenderer.Render("   "));
    }

    [Fact]
    public void Plain_text_renders()
    {
        Assert.NotNull(MarkdownRenderer.Render("just some plain text with no markdown"));
    }
}
