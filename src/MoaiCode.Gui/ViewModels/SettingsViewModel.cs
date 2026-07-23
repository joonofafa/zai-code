using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoaiCode.Config;
using MoaiCode.Localization;

namespace MoaiCode.Gui.ViewModels;

/// <summary>계정 · 모델 설정. 로그인 시 저장된 모델 목록에서 사용 모델을 바꾼다.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public string AccountName { get; }
    public string Email { get; }
    public ObservableCollection<string> Models { get; } = new();
    public IReadOnlyList<LanguageOption> Languages => L10n.SupportedLanguages;

    public string WindowTitle => L10n.Get("gui.settings.title");
    public string AccountLabel => L10n.Get("gui.settings.account");
    public string ModelLabel => L10n.Get("gui.settings.model");
    public string ModelHint => L10n.Get("gui.settings.modelHint");
    public string LanguageLabel => L10n.Get("gui.settings.language");
    public string LanguageHint => L10n.Get("gui.settings.languageHint");
    public string ReloginText => L10n.Get("gui.settings.relogin");
    public string CloseText => L10n.Get("gui.settings.close");
    public string SaveText => L10n.Get("gui.settings.save");

    [ObservableProperty] private string? _selectedModel;
    [ObservableProperty] private LanguageOption? _selectedLanguage;

    /// <summary>모델 저장 완료 시 발생(엔진 재구성 트리거).</summary>
    public event Action? Saved;

    public SettingsViewModel()
    {
        var s = SettingsLoader.Load(Directory.GetCurrentDirectory());

        AccountName = !string.IsNullOrWhiteSpace(s.Name)
            ? (string.IsNullOrWhiteSpace(s.OrgName) ? s.Name! : $"{s.Name} ({s.OrgName})")
            : (s.Account ?? L10n.Get("gui.settings.loginRequired"));
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
        SelectedLanguage = Languages.FirstOrDefault(x => x.Code == s.Language)
                           ?? Languages.First(x => x.Code == L10n.DefaultLanguage);
    }

    [RelayCommand]
    private void Save()
    {
        var values = new Dictionary<string, string?>();
        if (!string.IsNullOrEmpty(SelectedModel))
        {
            Environment.SetEnvironmentVariable("MOAI_MODEL", SelectedModel);
            values["model"] = SelectedModel;
        }

        if (SelectedLanguage is not null)
        {
            L10n.SetLanguage(SelectedLanguage.Code);
            Environment.SetEnvironmentVariable("MOAI_LANGUAGE", SelectedLanguage.Code);
            values["language"] = SelectedLanguage.Code;
        }

        if (values.Count > 0)
        {
            SettingsWriter.Set(values);
        }

        Saved?.Invoke();
    }
}
