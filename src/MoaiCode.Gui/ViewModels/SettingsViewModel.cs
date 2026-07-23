using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoaiCode.Config;

namespace MoaiCode.Gui.ViewModels;

/// <summary>계정 · 모델 설정. 로그인 시 저장된 모델 목록에서 사용 모델을 바꾼다.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public string AccountName { get; }
    public string Email { get; }
    public ObservableCollection<string> Models { get; } = new();

    [ObservableProperty] private string? _selectedModel;

    /// <summary>모델 저장 완료 시 발생(엔진 재구성 트리거).</summary>
    public event Action? Saved;

    public SettingsViewModel()
    {
        var s = SettingsLoader.Load(Directory.GetCurrentDirectory());

        AccountName = !string.IsNullOrWhiteSpace(s.Name)
            ? (string.IsNullOrWhiteSpace(s.OrgName) ? s.Name! : $"{s.Name} ({s.OrgName})")
            : (s.Account ?? "(로그인 필요)");
        Email = s.Account ?? string.Empty;

        foreach (var m in (s.AvailableModels ?? string.Empty)
                 .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Models.Add(m);
        }

        // 현재 모델이 목록에 없으면 표시용으로 추가.
        if (!string.IsNullOrEmpty(s.Model) && !Models.Contains(s.Model))
        {
            Models.Insert(0, s.Model);
        }

        SelectedModel = s.Model ?? Models.FirstOrDefault();
    }

    [RelayCommand]
    private void Save()
    {
        if (!string.IsNullOrEmpty(SelectedModel))
        {
            Environment.SetEnvironmentVariable("MOAI_MODEL", SelectedModel);
            SettingsWriter.Set(new Dictionary<string, string?> { ["model"] = SelectedModel });
        }

        Saved?.Invoke();
    }
}
