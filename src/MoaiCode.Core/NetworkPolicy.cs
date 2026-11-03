namespace MoaiCode.Core;

/// <summary>
/// 오프라인 모드(settings.json "offline": true 또는 MOAI_OFFLINE=1). 망분리 사내망처럼 외부로 나갈 수 없는 곳에서
/// 인터넷이 필요한 툴(WebFetch·WebSearch·ImageFetch·ImageCreate, ImageAnalysis 의 URL 입력)을 모델에게서 숨기고
/// 실행도 거절한다. 대화 모델 연결(사내 vLLM 등)은 건드리지 않는다 — 셸 명령의 네트워크는 망 자체가 막아야 한다.
/// </summary>
public static class NetworkPolicy
{
    private static volatile bool _offline;

    public static bool Offline
    {
        get => _offline;
        set => _offline = value;
    }
}
