using MoaiCode.Core.Agent;

namespace MoaiCode.Providers;

/// <summary>
/// 프로바이더 추상화 (TS의 게이트웨이 디스크립터 + 클라이언트 팩토리 대응).
/// Phase 1에서 OpenAI shim / Anthropic / Gemini / Ollama / Codex 구현 추가.
/// </summary>
public interface IProvider
{
    string Id { get; }
    IChatModel CreateModel(string model);
}
