using System.Text.Json;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.Office;
using Xunit;

namespace MoaiCode.Office.Tests;

// Office 툴의 계약(이름/읽기전용 분류/스키마)을 COM 없이 검증. 읽기전용 분류가 틀리면
// 편집 툴이 권한 게이트를 우회하거나 조회 툴이 불필요한 확인을 받는다.
public sealed class ToolContractTests
{
    private static readonly StaDispatcher Sta = new();

    public static IEnumerable<object[]> AllTools()
    {
        foreach (var t in OfficeTools.BuildTools(Sta))
        {
            yield return new object[] { t };
        }
    }

    [Fact]
    public void Registers_expected_tools_on_windows_shape()
    {
        var names = OfficeTools.BuildTools(Sta).Select(t => t.Name).ToList();
        Assert.Contains("PowerPointInspect", names);
        Assert.Contains("PowerPointEdit", names);
        Assert.Contains("ExcelInspect", names);
    }

    [Theory]
    [MemberData(nameof(AllTools))]
    public void Tool_metadata_is_valid(ITool tool)
    {
        Assert.False(string.IsNullOrWhiteSpace(tool.Name));
        Assert.False(string.IsNullOrWhiteSpace(tool.Description));
        Assert.Equal(JsonValueKind.Object, tool.InputSchema.ValueKind); // 스키마가 파싱됨
        Assert.False(tool.IsConcurrencySafe); // COM STA — 직렬화
    }

    [Theory]
    [InlineData("PowerPointInspect", true)]
    [InlineData("ExcelInspect", true)]
    [InlineData("PowerPointEdit", false)] // 편집 → 쓰기 게이트 통과 대상
    public void ReadOnly_classification(string name, bool readOnly)
    {
        var tool = OfficeTools.BuildTools(Sta).First(t => t.Name == name);
        Assert.Equal(readOnly, tool.IsReadOnly);
    }
}
