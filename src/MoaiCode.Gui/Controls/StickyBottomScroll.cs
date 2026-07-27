using Avalonia;
using Avalonia.Controls;

namespace MoaiCode.Gui.Controls;

/// <summary>
/// ScrollViewer 를 콘텐츠가 커지는 동안(새 항목 추가 또는 스트리밍으로 마지막 항목이 자라남)
/// 하단에 고정한다. 단, 사용자가 위로 스크롤하면 고정을 풀고, 다시 맨 아래로 오면 재고정한다.
///
/// 정석: ScrollChanged 는 Extent 갱신이 끝난 '완료된 레이아웃 패스'에서 발생하므로,
/// 그 안에서 Offset 을 설정하면 항상 정확하다(Dispatcher.Post / LayoutUpdated 프레임 세기 불필요).
/// 참고: Avalonia #9992, discussions/12931, scrollviewer-how-to.
/// </summary>
public sealed class StickyBottomScroll
{
    private readonly ScrollViewer _sv;
    private bool _stick = true;

    private StickyBottomScroll(ScrollViewer sv)
    {
        _sv = sv;
        _sv.ScrollChanged += OnScrollChanged;
    }

    public static StickyBottomScroll Attach(ScrollViewer sv) => new(sv);

    /// <summary>사용자가 메시지를 보낼 때 등, 강제로 하단에 재고정한다.</summary>
    public void StickToBottom()
    {
        _stick = true;
        ScrollToBottom();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // 순수 오프셋 변경(휠/키/드래그) → 사용자가 움직인 것. 하단 근처면 계속 따라가고 아니면 해제.
        if (e.ExtentDelta.Y == 0 && e.ViewportDelta.Y == 0 && e.OffsetDelta.Y != 0)
        {
            _stick = IsAtBottom();
            return;
        }

        // 콘텐츠가 커졌거나 뷰포트가 바뀜 → 고정 상태면 따라 내려간다.
        // 이 시점엔 Extent 가 이미 최신이라 Offset 설정이 정확하다.
        if ((e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0) && _stick)
        {
            ScrollToBottom();
        }
    }

    private bool IsAtBottom() =>
        _sv.Offset.Y >= _sv.Extent.Height - _sv.Viewport.Height - 2.0;

    private void ScrollToBottom() =>
        _sv.Offset = new Vector(_sv.Offset.X, _sv.Extent.Height - _sv.Viewport.Height);
}
