namespace MoaiCode.Core.Agent;

/// <summary>
/// 모델 호출 예외를 Core 레벨에서 분기할 수 있게 하는 계약.
/// 프로바이더 레이어(ProviderException 등)가 구현한다 — 의존성 방향은 Providers → Core.
/// QueryEngine은 구체 예외 타입을 모른 채 컨텍스트 초과 여부만 본다.
/// </summary>
public interface IModelException
{
    /// <summary>컨텍스트 길이 초과로 인한 실패인지 (반응형 컴팩션 트리거).</summary>
    bool IsContextOverflow { get; }
}
