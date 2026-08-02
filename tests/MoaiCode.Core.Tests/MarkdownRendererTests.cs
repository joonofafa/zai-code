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

    // CommonMark right-flanking 규칙: 닫는 ** 앞이 문장부호이고 뒤가 한글이면 강조로 닫히지 않아
    // 화면에 ** 가 그대로 노출됐다. 한국어는 따옴표 뒤에 조사를 붙여 쓰므로 매우 흔한 패턴.
    [Theory]
    [InlineData("**\"인용\"**까지 진행")]                    // 따옴표로 끝 + 조사
    [InlineData("**그 다음 요청은 \"1k 대결\"**였죠.")]      // 동일 패턴(문장 안)
    [InlineData("**(주의)**를 참고")]                        // 괄호로 끝 + 조사
    public void Bold_closing_after_punctuation_before_hangul_still_renders(string md)
        => Assert.DoesNotContain("**", RenderToText(md));

    [Theory]
    [InlineData("**bold** text")]
    [InlineData("**강조**가 안될까?")]
    [InlineData("그럼 **hard vs alpha**를 보자")]
    public void Normal_bold_still_renders(string md)
        => Assert.DoesNotContain("**", RenderToText(md));

    [Fact]
    public void Unpaired_asterisks_are_left_alone()
    {
        // 파이썬 **kwargs 처럼 짝이 없는 ** 는 굵게로 오인해 먹어버리면 안 된다.
        Assert.Contains("**kwargs", RenderToText("use **kwargs here"));
    }
}
