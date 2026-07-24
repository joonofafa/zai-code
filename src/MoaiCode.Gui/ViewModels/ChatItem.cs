using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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

/// <summary>라이브 편집 전 확인 카드(미리보기 게이트). 사용자가 [적용]/[취소] 를 누르면 Tcs 로 결과 전달.</summary>
public sealed partial class ConfirmItem : ChatItem
{
    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private bool _decided;
    [ObservableProperty] private string _resultLabel = string.Empty;

    /// <summary>게이트가 대기 중인 결정. 버튼 클릭 시 완료된다.</summary>
    public TaskCompletionSource<bool>? Tcs { get; init; }

    /// <summary>"계속 허용" 선택 시 호출 — 이후 편집을 세션 동안 자동 승인하도록 VM 에 알린다.</summary>
    public System.Action? OnApproveAll { get; init; }

    [RelayCommand] private void Approve() => Decide(true, "적용함");

    [RelayCommand] private void Reject() => Decide(false, "취소함");

    [RelayCommand]
    private void ApproveAll()
    {
        OnApproveAll?.Invoke();
        Decide(true, "적용함 · 이후 자동 승인");
    }

    private void Decide(bool ok, string label)
    {
        if (Decided)
        {
            return;
        }

        Decided = true;
        ResultLabel = label;
        Tcs?.TrySetResult(ok);
    }
}

/// <summary>클릭 가능한 추천 질문 묶음(편집 세션 진입 시 등).</summary>
public sealed class SuggestionItem : ChatItem
{
    public IReadOnlyList<string> Suggestions { get; init; } = Array.Empty<string>();
}

/// <summary>생성된 문서 카드.</summary>
public sealed partial class DocumentItem : ChatItem
{
    [ObservableProperty] private string _icon = "Icon.FileText"; // lucide 리소스 키
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
    public string Icon => Source == "org" ? "Icon.FolderOpen" : "Icon.Paperclip";
}
