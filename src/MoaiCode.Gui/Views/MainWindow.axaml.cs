using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MoaiCode.Gui.Controls;
using MoaiCode.Gui.ViewModels;

namespace MoaiCode.Gui.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _hooked;
    private StickyBottomScroll? _sticky;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += OnDataContextChanged;

        // 채팅 스크롤을 하단 고정(sticky) — ScrollChanged 기반. 스트리밍/새 항목에 정확히 따라가고
        // 사용자가 위로 올리면 멈춘다(Avalonia 정석; ScrollToEnd 프레임 세기·Offset 강제 폐기).
        var sv = this.FindControl<ScrollViewer>("ChatScroll");
        if (sv is not null)
        {
            _sticky = StickyBottomScroll.Attach(sv);
        }
    }

    // ViewModel 이 스크롤을 요청하면(메시지 전송 등) 하단에 강제 재고정.
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_hooked is not null)
        {
            _hooked.ScrollToEndRequested -= StickToBottom;
        }

        _hooked = DataContext as MainViewModel;
        if (_hooked is not null)
        {
            _hooked.ScrollToEndRequested += StickToBottom;
        }
    }

    private void StickToBottom() => _sticky?.StickToBottom();

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

    // 공유 폴더 연결 — 폴더 선택 → ViewModel 이 설정 저장 + 즉시 감시 시작.
    private async void OnAddFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "공유할 폴더 선택",
            AllowMultiple = false,
        });

        var folder = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(folder))
        {
            vm.AddFolder(folder);
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
