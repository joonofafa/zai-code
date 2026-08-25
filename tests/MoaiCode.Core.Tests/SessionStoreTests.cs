using System.Collections.Immutable;
using System.Text.Json;
using MoaiCode.Core.Messages;
using MoaiCode.Persistence;
using Xunit;

namespace MoaiCode.Core.Tests;

public class SessionStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly SessionStore _store;

    public SessionStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "occs-sess-" + Guid.NewGuid().ToString("n"));
        _store = new SessionStore(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

    [Fact]
    public async Task Roundtrips_all_message_types_with_polymorphism()
    {
        using var argsDoc = JsonDocument.Parse("""{"command":"ls"}""");
        var original = new List<Message>
        {
            new SystemMessage("sys prompt"),
            new UserMessage("hello"),
            new AssistantMessage(ImmutableList.Create<ContentBlock>(
                new TextBlock("running a tool"),
                new ToolUseBlock("call_1", "Bash", argsDoc.RootElement.Clone()))),
            new ToolResultMessage("call_1", "file1\nfile2", IsError: false),
        };

        await _store.SaveAsync("s1", original);
        var loaded = await _store.LoadAsync("s1");

        Assert.Equal(4, loaded.Count);
        Assert.IsType<SystemMessage>(loaded[0]);
        Assert.Equal("hello", Assert.IsType<UserMessage>(loaded[1]).Text);

        var asst = Assert.IsType<AssistantMessage>(loaded[2]);
        Assert.Equal(2, asst.Content.Count);
        Assert.Equal("running a tool", Assert.IsType<TextBlock>(asst.Content[0]).Text);
        var tu = Assert.IsType<ToolUseBlock>(asst.Content[1]);
        Assert.Equal("Bash", tu.Name);
        Assert.Equal("ls", tu.Input.GetProperty("command").GetString());

        var tr = Assert.IsType<ToolResultMessage>(loaded[3]);
        Assert.Equal("call_1", tr.ToolUseId);
        Assert.Contains("file1", tr.Output);
    }

    [Fact]
    public async Task Load_missing_session_returns_empty()
    {
        var loaded = await _store.LoadAsync("does-not-exist");
        Assert.Empty(loaded);
    }

    [Fact]
    public async Task Lists_saved_sessions()
    {
        await _store.SaveAsync("a", new List<Message> { new UserMessage("x") });
        await _store.SaveAsync("b", new List<Message> { new UserMessage("y") });

        var sessions = _store.ListSessions();
        Assert.Contains("a", sessions);
        Assert.Contains("b", sessions);
    }

    [Fact]
    public async Task Title_is_the_last_user_prompt()
    {
        await _store.SaveAsync("s", new List<Message>
        {
            new SystemMessage("sys"),
            new UserMessage("첫 질문"),
            new AssistantMessage(System.Collections.Immutable.ImmutableList.Create<ContentBlock>(new TextBlock("ok"))),
            new UserMessage("<system-reminder>noise</system-reminder>"),
            new UserMessage("마지막 질문"),
        });

        var info = Assert.Single(await _store.ListInfosAsync());
        Assert.Equal("마지막 질문", info.Title); // 첫 질문/리마인더가 아니라 마지막 프롬프트
    }

    [Fact]
    public async Task Title_falls_back_to_original_request_from_summary_when_no_plain_prompt()
    {
        // 압축 세션: 일반 프롬프트 없이 요약 + 리마인더만.
        var summary = "[Summary of earlier conversation]\n최초 사용자 요청:\n\n> `USB 외장하드 확인해줘`\n\n중략...";
        await _store.SaveAsync("compacted", new List<Message>
        {
            new SystemMessage("sys"),
            new UserMessage(summary),
            new UserMessage("<system-reminder>focus</system-reminder>"),
        });

        var info = Assert.Single(await _store.ListInfosAsync());
        Assert.Equal("USB 외장하드 확인해줘", info.Title); // 요약서 첫 인용문에서 추출, (제목 없음) 아님
    }

    [Fact]
    public async Task Title_falls_back_to_original_request_anchor_in_reminder()
    {
        // 요약에 인용문이 없고, 리마인더의 'Original request:' 앵커만 있는 압축 세션.
        await _store.SaveAsync("anchored", new List<Message>
        {
            new SystemMessage("sys"),
            new UserMessage("[Summary of earlier conversation]\n1. Primary Request and Intent\n- 코드베이스 분석 진행."),
            new UserMessage("<system-reminder>\nStay focused.\n\nOriginal request: 커밋된 내용 3개 코드 리뷰해봐\n</system-reminder>"),
        });

        var info = Assert.Single(await _store.ListInfosAsync());
        Assert.Equal("커밋된 내용 3개 코드 리뷰해봐", info.Title);
    }

    [Fact]
    public async Task ListInfos_caps_at_99_most_recent()
    {
        for (var i = 0; i < 105; i++)
        {
            await _store.SaveAsync($"s{i:000}", new List<Message> { new UserMessage($"q{i}") });
        }

        var infos = await _store.ListInfosAsync();
        Assert.Equal(99, infos.Count); // 최대 99개만 노출
    }

    [Fact]
    public async Task Delete_removes_session_file_and_from_listing()
    {
        await _store.SaveAsync("s1", new List<Message> { new UserMessage("keep") });
        await _store.SaveAsync("s2", new List<Message> { new UserMessage("remove me") });
        Assert.True(File.Exists(_store.PathFor("s2")));

        var ok = _store.Delete("s2");

        Assert.True(ok);
        Assert.False(File.Exists(_store.PathFor("s2")));
        var ids = (await _store.ListInfosAsync()).Select(i => i.Id).ToList();
        Assert.Contains("s1", ids);
        Assert.DoesNotContain("s2", ids);
    }

    [Fact]
    public void Delete_missing_session_is_noop_success()
    {
        Assert.True(_store.Delete("does-not-exist"));
    }

    [Fact]
    public async Task Retention_off_by_default_keeps_all_sessions()
    {
        // 생성자 기본 retainCount=0·retainDays=0 → 정리 안 함.
        for (var i = 0; i < 5; i++)
        {
            await _store.SaveAsync($"s{i}", new List<Message> { new UserMessage($"q{i}") });
        }

        Assert.Equal(5, _store.ListSessions().Count);
    }

    [Fact]
    public async Task Retention_count_deletes_sessions_beyond_limit_keeping_current()
    {
        Directory.CreateDirectory(_dir);
        // 오래된 더미 세션 5개 — 서로 다른 수정시각(오래된→최근)으로 정렬 안정성 확보.
        var baseTime = DateTime.UtcNow.AddDays(-10);
        for (var i = 0; i < 5; i++)
        {
            var p = Path.Combine(_dir, $"old{i}.jsonl");
            await File.WriteAllTextAsync(p, "{}");
            File.SetLastWriteTimeUtc(p, baseTime.AddMinutes(i));
        }

        var store = new SessionStore(_dir, retainCount: 3);
        await store.SaveAsync("current", new List<Message> { new UserMessage("now") });

        var remaining = store.ListSessions().ToHashSet();
        Assert.Equal(3, remaining.Count);         // current(최신) + old4 + old3
        Assert.Contains("current", remaining);    // 현재 세션은 항상 보존
        Assert.Contains("old4", remaining);       // 가장 최근 더미
        Assert.DoesNotContain("old0", remaining); // 가장 오래된 더미는 삭제
    }

    [Fact]
    public async Task Retention_days_deletes_files_older_than_cutoff_keeping_current()
    {
        Directory.CreateDirectory(_dir);
        var ancient = Path.Combine(_dir, "ancient.jsonl");
        await File.WriteAllTextAsync(ancient, "{}");
        File.SetLastWriteTimeUtc(ancient, DateTime.UtcNow.AddDays(-100));

        var recent = Path.Combine(_dir, "recent.jsonl");
        await File.WriteAllTextAsync(recent, "{}");
        File.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddDays(-1));

        var store = new SessionStore(_dir, retainDays: 30);
        await store.SaveAsync("current", new List<Message> { new UserMessage("now") });

        var remaining = store.ListSessions().ToHashSet();
        Assert.DoesNotContain("ancient", remaining); // 100일 > 30일 → 삭제
        Assert.Contains("recent", remaining);        // 1일 < 30일 → 유지
        Assert.Contains("current", remaining);
    }

    [Fact]
    public async Task Sweep_reapplies_owner_only_permissions_to_preexisting_files()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix 권한 전용 — Windows 에서는 no-op 이라 단언 생략.
        }

        Directory.CreateDirectory(_dir);
        var stale = Path.Combine(_dir, "stale.jsonl");
        await File.WriteAllTextAsync(stale, "{}");
        File.SetUnixFileMode(stale, // 0644 — 권한 강제 이전 파일 흉내
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var store = new SessionStore(_dir);
        await store.SaveAsync("current", new List<Message> { new UserMessage("now") });

        var groupOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite;
        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(stale) & groupOther); // 그룹/타인 비트 제거
    }
}
