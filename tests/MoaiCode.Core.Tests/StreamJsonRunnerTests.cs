using MoaiCode.Cli;
using Xunit;

namespace MoaiCode.Core.Tests;

// --input-format stream-json 의 stdin 한 줄 파싱. claude CLI 의 미문서화 포맷을 따라가므로
// 형태가 조금씩 다른 입력(문자열 content, 여러 text 블록)도 받아야 한다.
public sealed class StreamJsonRunnerTests
{
    [Fact]
    public void Reads_text_blocks_from_a_user_turn()
    {
        var line = """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"안녕"}]}}""";
        Assert.Equal("안녕", StreamJsonRunner.ExtractUserText(line));
    }

    [Fact]
    public void Concatenates_multiple_text_blocks_and_skips_other_block_types()
    {
        var line = """
        {"type":"user","message":{"role":"user","content":[
          {"type":"text","text":"a"},
          {"type":"image","source":{}},
          {"type":"text","text":"b"}]}}
        """;
        Assert.Equal("ab", StreamJsonRunner.ExtractUserText(line));
    }

    [Fact]
    public void Accepts_content_given_as_a_plain_string()
    {
        var line = """{"type":"user","message":{"role":"user","content":"바로 문자열"}}""";
        Assert.Equal("바로 문자열", StreamJsonRunner.ExtractUserText(line));
    }

    [Theory]
    // 비-user 이벤트(브리지가 자기 출력을 되돌려 넣는 경우), 빈 줄, 깨진 JSON, 텍스트 없는 턴은
    // 턴을 만들지 않고 조용히 건너뛴다 — 여기서 예외가 나면 영속 루프가 죽는다.
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"text","text":"x"}]}}""")]
    [InlineData("""{"type":"result","result":"x"}""")]
    [InlineData("""{"type":"user","message":{"role":"user","content":[]}}""")]
    [InlineData("""{"type":"user"}""")]
    [InlineData("[]")]
    [InlineData("""{"type":1}""")]
    [InlineData("""{"type":"user","message":[]}""")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("   ")]
    public void Ignores_lines_that_are_not_a_user_turn(string line)
        => Assert.Null(StreamJsonRunner.ExtractUserText(line));

    [Fact]
    public void Builds_tool_result_content_with_images_as_block_array()
    {
        var images = new List<(string Base64, string MediaType)> { ("QUJD", "image/png") };
        var content = StreamJsonRunner.BuildToolResultContent("saved to a.png", images);

        Assert.Equal(2, content.Count);
        Assert.Equal("text", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("saved to a.png", content[0]!["text"]!.GetValue<string>());
        Assert.Equal("image", content[1]!["type"]!.GetValue<string>());
        var source = content[1]!["source"]!;
        Assert.Equal("base64", source["type"]!.GetValue<string>());
        Assert.Equal("image/png", source["media_type"]!.GetValue<string>());
        Assert.Equal("QUJD", source["data"]!.GetValue<string>());
    }

    [Fact]
    public void Builds_tool_result_content_defaults_missing_media_type_to_png()
    {
        var images = new List<(string Base64, string MediaType)> { ("QUJD", "") };
        var content = StreamJsonRunner.BuildToolResultContent("", images);

        // 본문 텍스트가 비면 text 블록 없이 이미지만.
        Assert.Single(content);
        Assert.Equal("image/png", content[0]!["source"]!["media_type"]!.GetValue<string>());
    }
}
