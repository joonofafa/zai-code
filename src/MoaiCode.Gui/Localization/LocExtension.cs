using System;
using Avalonia.Markup.Xaml;
using MoaiCode.Localization;

namespace MoaiCode.Gui.Localization;

/// <summary>
/// XAML 로컬라이제이션 마크업 확장. 사용: <c>Text="{l10n:Loc gui.folders.title}"</c>.
/// 앱 시작 시 <c>L10n.SetLanguage</c> 로 언어가 정해진 뒤 XAML 이 로드되므로 로드 시점에
/// <c>L10n.Get(key)</c> 로 정적 해석한다. 런타임 언어 전환은 창 재로드가 필요(설정 저장 후 재시작 안내).
/// 정적 문자열용 — 서식/동적 문자열은 VM 에서 <c>L10n.Get</c> 로 처리한다.
/// </summary>
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => L10n.Get(Key);
}
