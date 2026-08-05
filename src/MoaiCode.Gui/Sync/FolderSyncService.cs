using System;
using System.Collections.Generic;
using System.IO;

namespace MoaiCode.Gui.Sync;

/// <summary>연결된 로컬 폴더 하나. Visibility 는 하위 호환용으로 남겨 두되(설정 파일), 자동 업로드가
/// 없어진 뒤로는 의미가 없다(추후 로컬 인덱싱에서 재사용 가능). orgId 도 미사용.</summary>
public sealed record ConnectedFolder(string Path, string? OrgId, string Visibility);

/// <summary>
/// 로컬 폴더 등록/목록만 담당한다. 예전에는 폴더를 감시해 조직 문서함에 자동 업로드했으나
/// 제거됨 — 서버(조직 문서함)로 보내는 것은 사용자가 필요할 때 직접 업로드한다. 폴더 등록은
/// 추후 '로컬 인덱싱(로컬 청킹 + 임베딩 + 로컬 벡터 검색)' 이 붙을 자리로 남겨 둔다.
/// </summary>
public sealed class FolderSyncService : IDisposable
{
    private readonly List<ConnectedFolder> _folders = new();

    /// <summary>트레이/UI 표시용 상태 메시지.</summary>
    public event Action<string>? Status;

    /// <summary>앱에서 만든 단일 인스턴스(VM 이 폴더 목록을 제어하기 위해 접근).</summary>
    public static FolderSyncService? Instance { get; private set; }

    public FolderSyncService() => Instance = this;

    public IReadOnlyList<ConnectedFolder> Folders => _folders;

    public int FolderCount => _folders.Count;

    public void Start(IEnumerable<ConnectedFolder> folders)
    {
        foreach (var f in folders)
        {
            Connect(f);
        }

        Status?.Invoke(StatusText());
    }

    /// <summary>폴더 하나를 등록한다(자동 업로드 없음). 존재하지 않는 경로는 등록하지 않는다.</summary>
    public void Connect(ConnectedFolder folder)
    {
        if (!Directory.Exists(folder.Path))
        {
            Status?.Invoke($"폴더 없음: {folder.Path}");
            return;
        }

        _folders.Add(folder);
        Status?.Invoke(StatusText());
    }

    /// <summary>폴더 등록을 해제한다.</summary>
    public void Disconnect(string path)
    {
        _folders.RemoveAll(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        Status?.Invoke(StatusText());
    }

    private string StatusText() =>
        _folders.Count == 0 ? "연결된 폴더 없음" : $"연결된 폴더 {_folders.Count}개";

    public void Dispose()
    {
        _folders.Clear();
    }
}
