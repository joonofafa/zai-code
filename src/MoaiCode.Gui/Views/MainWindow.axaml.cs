using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MoaiCode.Gui.ViewModels;

namespace MoaiCode.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);

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
}
