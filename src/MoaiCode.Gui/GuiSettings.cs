using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MoaiCode.Gui;

/// <summary>GUI 사용자 설정(~/.moai/gui-settings.json). 최초 실행 테마 선택 등.</summary>
public sealed class GuiSettings
{
    public string? Theme { get; set; } // "light" | "dark" | null(미선택)

    /// <summary>스킬 기능 마스터 스위치. off 면 SkillTool 을 아예 등록하지 않는다. 기본 on.</summary>
    public bool SkillsEnabled { get; set; } = true;

    /// <summary>문서함과 연결된 공유 폴더 목록(동기화 대상).</summary>
    public List<Sync.ConnectedFolder> ConnectedFolders { get; set; } = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".moai", "gui-settings.json");

    public static GuiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(FilePath)) ?? new GuiSettings();
            }
        }
        catch
        {
            // 손상/권한 문제 → 기본값
        }

        return new GuiSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 저장 실패는 치명적 아님
        }
    }
}
