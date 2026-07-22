using CommunityToolkit.Mvvm.ComponentModel;

namespace MoaiCode.Gui.ViewModels;

/// <summary>채팅 스트림의 한 항목. 타입별 DataTemplate 로 렌더된다.</summary>
public abstract class ChatItem : ObservableObject
{
}

public sealed partial class UserItem : ChatItem
{
    [ObservableProperty] private string _text = string.Empty;
}

public sealed partial class AssistantItem : ChatItem
{
    [ObservableProperty] private string _text = string.Empty;
}

/// <summary>진행 배지("엑셀 문서 만드는 중…" / 완료).</summary>
public sealed partial class ActivityItem : ChatItem
{
    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private bool _done;
}

/// <summary>생성된 문서 카드.</summary>
public sealed partial class DocumentItem : ChatItem
{
    [ObservableProperty] private string _icon = "📄";
    [ObservableProperty] private string _kind = string.Empty;    // 엑셀 / 워드 / 발표
    [ObservableProperty] private string _fileName = string.Empty;
    [ObservableProperty] private string _path = string.Empty;
}

public sealed partial class SessionItem : ObservableObject
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _when = string.Empty;
}

/// <summary>모드 B 참조 문서(로컬/조직). 추출한 원문을 생성 컨텍스트에 주입한다.</summary>
public sealed class ReferenceItem
{
    public string DisplayName { get; init; } = string.Empty;
    public string Source { get; init; } = "local"; // local | org
    public string Text { get; init; } = string.Empty;
    public string? Path { get; init; } // local: 파일경로 / org: documentId
    public string Icon => Source == "org" ? "🗂" : "📎";
}
