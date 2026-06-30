namespace MoaiCode.Core.Agent.Prompts;

/// <summary>
/// 시스템 프롬프트 조립에 필요한 런타임 컨텍스트 (OpenClaude computeSimpleEnvInfo + getUserContext 대응).
/// IO(git/파일읽기)는 호출측(부트스트랩)에서 채워 넣고, SystemPromptBuilder는 순수하게 조립만 한다.
/// </summary>
public sealed record PromptContext
{
    public required string WorkingDirectory { get; init; }
    public bool IsGitRepo { get; init; }
    public string Platform { get; init; } = "";
    public string OsVersion { get; init; } = "";
    public string CurrentDate { get; init; } = "";
    public string? ModelDescription { get; init; }
    public string? ClaudeMd { get; init; }
    public string? RepoMap { get; init; }
    public string? OutputStyle { get; init; }
    public IReadOnlyList<string> AdditionalDirectories { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ToolNames { get; init; } = Array.Empty<string>();
}
