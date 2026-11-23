using MoaiCode.Config;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>감사 로그의 해시 이어짐 — 중간 줄을 고치거나 빼거나 끼워 넣으면 검증이 그 자리를 짚는다.</summary>
[Collection("EnvMutating")]
public sealed class AuditLogChainTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "mate-audit-chain-" + Guid.NewGuid().ToString("n") + ".jsonl");
    private readonly string _previous = AuditLog.FilePath;

    public AuditLogChainTests() => AuditLog.FilePath = _file;

    public void Dispose()
    {
        AuditLog.FilePath = _previous;
        File.Delete(_file);
    }

    private static void Write(string command) =>
        Assert.True(AuditLog.TryWrite(AuditLog.Create("Bash", command, AuditLog.Outcomes.Ok, AuditLog.Approvals.User, null, "act", null)));

    private void WriteThree()
    {
        Write("echo one");
        Write("echo two");
        Write("echo three");
    }

    [Fact]
    public void Verify_untouchedLog_isChainedFromGenesis()
    {
        WriteThree();

        var r = AuditLog.Verify();

        Assert.Null(r.BadLine);
        Assert.Equal(3, r.Lines);
        Assert.Equal(3, r.Chained);
        Assert.True(r.Anchored);
        Assert.Contains("\"prev\":\"" + AuditLog.Genesis + "\"", File.ReadLines(_file).First());
        Assert.Equal(64, r.Head!.Length);
    }

    [Fact]
    public void Verify_editedMiddleLine_pointsAtTheNextLine()
    {
        WriteThree();
        var lines = File.ReadAllLines(_file);
        lines[1] = lines[1].Replace("echo two", "rm -rf /data");
        File.WriteAllLines(_file, lines);

        Assert.Equal(3, AuditLog.Verify().BadLine);
    }

    [Fact]
    public void Verify_removedOrInsertedLine_isDetected()
    {
        WriteThree();
        var lines = File.ReadAllLines(_file);

        File.WriteAllLines(_file, new[] { lines[0], lines[2] });
        Assert.Equal(2, AuditLog.Verify().BadLine);

        // prev 없는 줄을 끼워 넣어도, 다른 줄을 복사해 넣어도 걸린다.
        File.WriteAllLines(_file, new[] { lines[0], """{"ts":"2026-01-01T00:00:00+09:00","tool":"Bash","target":"x"}""", lines[1], lines[2] });
        Assert.Equal(2, AuditLog.Verify().BadLine);
        File.WriteAllLines(_file, new[] { lines[0], lines[1], lines[1], lines[2] });
        Assert.Equal(3, AuditLog.Verify().BadLine);
    }

    [Fact]
    public void Verify_recordsFromBeforeChaining_areSkippedAndLinkedTo()
    {
        File.WriteAllText(_file, """{"ts":"2026-11-01T00:00:00+09:00","tool":"Bash","target":"old"}""" + "\n");
        Write("echo new");
        Write("echo newer");

        var r = AuditLog.Verify();
        Assert.Null(r.BadLine);
        Assert.Equal(3, r.Lines);
        Assert.Equal(2, r.Chained);

        // 옛 줄을 고치면 그 줄에 이어 붙인 첫 줄에서 걸린다.
        var lines = File.ReadAllLines(_file);
        lines[0] = lines[0].Replace("old", "changed");
        File.WriteAllLines(_file, lines);
        Assert.Equal(2, AuditLog.Verify().BadLine);
    }

    [Fact]
    public void Verify_logWhoseHeadWasTrimmed_isValidButNotAnchored()
    {
        WriteThree();
        File.WriteAllLines(_file, File.ReadAllLines(_file).Skip(1));

        var r = AuditLog.Verify();
        Assert.Null(r.BadLine);
        Assert.False(r.Anchored);

        // 줄바꿈 없이 끝난 파일(쓰다 끊긴 줄)에도 이어 붙는다.
        File.WriteAllText(_file, File.ReadAllText(_file).TrimEnd('\n'));
        Write("echo four");
        Assert.Null(AuditLog.Verify().BadLine);
        Assert.Equal(3, AuditLog.Verify().Lines);
    }
}
