using System.Text.Json;
using System.Text.Json.Serialization;

namespace MoaiCode.Core.Messages;

public enum MessageRole { System, User, Assistant, Tool }

/// <summary>대화 메시지의 판별 유니온 기반 클래스 (TS의 Message union 대응).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(SystemMessage), "system")]
[JsonDerivedType(typeof(UserMessage), "user")]
[JsonDerivedType(typeof(AssistantMessage), "assistant")]
[JsonDerivedType(typeof(ToolResultMessage), "tool")]
public abstract record Message(MessageRole Role)
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record SystemMessage(string Text) : Message(MessageRole.System);

public sealed record UserMessage(string Text) : Message(MessageRole.User);

public sealed record AssistantMessage(IReadOnlyList<ContentBlock> Content)
    : Message(MessageRole.Assistant);

public sealed record ToolResultMessage(string ToolUseId, string Output, bool IsError = false)
    : Message(MessageRole.Tool);

/// <summary>assistant 메시지 내부 콘텐츠 블록 (텍스트 / 툴 호출).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(ToolUseBlock), "tool_use")]
public abstract record ContentBlock;

public sealed record TextBlock(string Text) : ContentBlock;

public sealed record ToolUseBlock(string Id, string Name, JsonElement Input) : ContentBlock;

/// <summary>
/// 프로바이더가 툴 인자를 JSON 으로 해석하지 못했을 때(잘림 등) Input 객체에 싣는 표식 키 — 값은 원문 앞부분.
/// 엔진은 이 호출을 실행하지 않고 오류 결과로 돌려준다. 예전엔 빈 객체 {} 로 바꿔 툴이 인자 없이 실행됐고,
/// 기록에도 {} 가 남아 모델이 "보낸 적 없는 호출"의 오류를 보고 같은 호출을 되풀이했다.
/// </summary>
public static class ToolArguments
{
    public const string InvalidKey = "__invalid_json_arguments";

    public static bool IsInvalid(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(InvalidKey, out _);
}

/// <summary>토큰 사용량 (비용 트래킹용).</summary>
public sealed record Usage(
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens = 0,
    int CacheCreationTokens = 0);
