using MoaiCode.Core.Tools;
using MoaiCode.Tools.Files;
using MoaiCode.Tools.Knowledge;
using MoaiCode.Tools.Search;
using MoaiCode.Tools.Web;

namespace MoaiCode.Tools;

/// <summary>
/// 활성 툴 집합을 조립 (TS의 tools.ts 대응). 피처 토글/권한 필터는 Phase 5에서.
/// Bash 등 destructive 툴은 별도 어셈블리(MoaiCode.Tools.Bash)에서 합류.
/// </summary>
public static class ToolRegistry
{
    public static IReadOnlyList<ITool> BuiltIn { get; } = new List<ITool>
    {
        new FileReadTool(),
        new FileWriteTool(),
        new FileEditTool(),
        new GlobTool(),
        new GrepTool(),
        new WebFetchTool(),
        new WebSearchTool(),
        new OrgDocsTool(),
        new OrgDocsListTool(),
        new OrgDocsUploadTool(),
        new OrgDatasListTool(),
        new OrgDatasTool(),
    };
}
