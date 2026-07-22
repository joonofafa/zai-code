using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MoaiCode.Gui.Sync;

/// <summary>
/// 폴더 동기화 상태(~/.moai/sync-manifest.json). 이미 올린 파일을 크기+수정시각으로 식별해
/// 미변경 파일 재업로드(→ 서버 재임베딩 낭비·문서함 중복)를 막는다. 변경 추적용 docId 도 보관.
/// </summary>
public sealed class SyncManifest
{
    public Dictionary<string, Entry> Files { get; set; } = new(StringComparer.Ordinal);

    public sealed class Entry
    {
        public long Size { get; set; }
        public long Mtime { get; set; }
        public string? DocId { get; set; }
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".moai", "sync-manifest.json");

    public static SyncManifest Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<SyncManifest>(File.ReadAllText(FilePath)) ?? new SyncManifest();
            }
        }
        catch
        {
            // 손상 → 새로 시작
        }

        return new SyncManifest();
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

    public bool IsUnchanged(string path)
    {
        var fi = new FileInfo(path);
        return Files.TryGetValue(path, out var e) && e.Size == fi.Length && e.Mtime == fi.LastWriteTimeUtc.Ticks;
    }

    public void MarkUploaded(string path, string? docId)
    {
        var fi = new FileInfo(path);
        Files[path] = new Entry { Size = fi.Length, Mtime = fi.LastWriteTimeUtc.Ticks, DocId = docId };
    }
}
