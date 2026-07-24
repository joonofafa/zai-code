using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MoaiCode.Gui.ViewModels;

namespace MoaiCode.Gui.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _hooked;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += OnDataContextChanged;
    }

    // 자동 스크롤 — ViewModel 의 요청마다 채팅을 맨 아래로.
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_hooked is not null)
        {
            _hooked.ScrollToEndRequested -= ScrollChatToEnd;
        }

        _hooked = DataContext as MainViewModel;
        if (_hooked is not null)
        {
            _hooked.ScrollToEndRequested += ScrollChatToEnd;
        }
    }

    private void ScrollChatToEnd()
    {
        // 새 콘텐츠 배치 후 스크롤. 배치 타이밍에 따라 한 번으로는 끝까지 못 가는 경우가 있어
        // Background(배치 직후)와 Loaded(레이아웃 확정 후) 두 우선순위로 각각 호출한다.
        void Scroll() => this.FindControl<ScrollViewer>("ChatScroll")?.ScrollToEnd();
        Dispatcher.UIThread.Post(Scroll, DispatcherPriority.Background);
        Dispatcher.UIThread.Post(Scroll, DispatcherPriority.Loaded);
    }

    // 참조 문서 첨부(모드 B) — 로컬 파일 다중 선택 → ViewModel 에 원문 추출 위임.
    private async void OnAttachClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "참조 문서 선택",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("문서")
                {
                    Patterns = new[] { "*.docx", "*.xlsx", "*.pptx", "*.pdf", "*.txt", "*.md", "*.csv" },
                },
            },
        });

        foreach (var f in files)
        {
            var path = f.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                vm.AddReference(path);
            }
        }
    }

    // 활성 대상 칩(App 버튼) → 연결 해제 확인 팝업의 [연결 해제]. Flyout 을 먼저 닫아
    // 해제 후 빈 값('( · )')으로 재표시되는 것을 막는다.
    private void OnDetachConfirm(object? sender, RoutedEventArgs e)
    {
        (this.FindControl<Button>("DocButton")?.Flyout as Avalonia.Controls.Flyout)?.Hide();
        if (DataContext is MainViewModel vm)
        {
            vm.DetachDocCommand.Execute(null);
        }
    }

    // 홈 "새로운 오피스 문서 작업" — 열린 Office 목록 + 앱 실행 창.
    // 열린 문서를 고르면 그 문서로 COM 세션을 열고 채팅을 시작한다.
    private async void OnOfficeDocClick(object? sender, RoutedEventArgs e)
    {
        var doc = await new OfficeLauncherWindow().PickAsync(this);
        if (doc is not null && DataContext is MainViewModel vm)
        {
            vm.OpenOfficeSession(doc);
        }
    }

    // 조직 문서함 검색(모드 B) — RAG 검색 다이얼로그 → 선택 스니펫을 참조로 첨부.
    private async void OnOrgClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var picked = await new OrgSearchWindow().PickAsync(this);
        if (picked is null)
        {
            return;
        }

        foreach (var reference in picked)
        {
            vm.AddOrgReference(reference);
        }
    }
}
