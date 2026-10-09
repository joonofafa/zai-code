using System.Text;
using System.Text.Json;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.Files;
using MoaiCode.Tools.Search;
using Xunit;

namespace MoaiCode.Core.Tests;

public class FileToolsTests : IDisposable
{
    private readonly string _dir;

    public FileToolsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "occs-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private async Task<(string Output, bool IsError)> Run(ITool tool, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        var err = false;
        await foreach (var p in tool.ExecuteAsync(doc.RootElement, new ToolContext(_dir, PermissionMode.Auto), default))
        {
            if (p is ToolOutput o)
            {
                sb.Append(o.Text);
                if (o.IsError)
                {
                    err = true;
                }
            }
        }

        return (sb.ToString(), err);
    }

    private static string EditJson(string path, string oldS, string newS) =>
        JsonSerializer.Serialize(new { path, old_string = oldS, new_string = newS });

    // Edit/Write 가 항상 BOM 없는 UTF-8 로 써서 BOM 이 사라지고 UTF-16 파일(.ps1·.reg 등)이 재인코딩됐다.
    [Fact]
    public async Task Edit_preserves_utf8_bom_and_utf16_encoding()
    {
        var bomPath = Path.Combine(_dir, "bom.txt");
        File.WriteAllText(bomPath, "hello 세계\n", new UTF8Encoding(true));
        var r = await Run(new FileEditTool(), EditJson(bomPath, "hello", "bye"));
        Assert.False(r.IsError, r.Output);
        var bytes = File.ReadAllBytes(bomPath);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal("bye 세계\n", new UTF8Encoding(true).GetString(bytes[3..]));

        var u16 = Path.Combine(_dir, "u16.ps1");
        File.WriteAllText(u16, "Write-Host 'a'\r\n", Encoding.Unicode);   // UTF-16 LE + BOM
        r = await Run(new FileEditTool(), EditJson(u16, "'a'", "'b'"));
        Assert.False(r.IsError, r.Output);
        bytes = File.ReadAllBytes(u16);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes[..2]);
        Assert.Equal("Write-Host 'b'\r\n", Encoding.Unicode.GetString(bytes[2..]));

        // Read 도 UTF-16 을 제대로 풀어 보여준다(예전엔 바이트를 UTF-8 로 읽어 깨졌다).
        var read = await Run(new FileReadTool(), JsonSerializer.Serialize(new { path = u16 }));
        Assert.Contains("Write-Host 'b'", read.Output);
    }

    // 레거시 인코딩(CP949 등) 파일은 UTF-8 로 읽으면 U+FFFD 로 바뀌어 저장 시 파일 전체가 깨진다 — 편집을 거부한다.
    [Fact]
    public async Task Edit_refuses_files_that_are_not_valid_utf8()
    {
        var path = Path.Combine(_dir, "legacy.txt");
        var original = new byte[] { (byte)'a', (byte)'=', 0xC7, 0xD1, 0xB1, 0xDB, (byte)'\n' };   // "a=한글" in CP949
        File.WriteAllBytes(path, original);
        var r = await Run(new FileEditTool(), EditJson(path, "a=", "b="));
        Assert.True(r.IsError);
        Assert.Contains("not valid UTF-8", r.Output);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    // CRLF 파일에서 old_string 은 LF 인데 new_string 에 CRLF 가 섞이면 \r\r\n 이 됐다.
    [Fact]
    public async Task Edit_on_crlf_file_does_not_double_carriage_returns()
    {
        var path = Path.Combine(_dir, "crlf.txt");
        File.WriteAllText(path, "one\r\ntwo\r\n");
        var r = await Run(new FileEditTool(), EditJson(path, "one\ntwo", "uno\r\ndos"));
        Assert.False(r.IsError, r.Output);
        Assert.Equal("uno\r\ndos\r\n", File.ReadAllText(path));
    }

    // 읽기에 실패한 파일은 '읽음'이 아니다 — 예전엔 시도만으로 표시돼, 내용을 못 본 파일을 Write 가 덮어쓸 수 있었다.
    [Fact]
    public async Task Failed_read_does_not_satisfy_read_before_write()
    {
        var path = Path.Combine(_dir, "broken.docx");
        File.WriteAllText(path, "not a zip");
        var ctx = new ToolContext(_dir, PermissionMode.Auto, new ReadTracker());

        using var readDoc = JsonDocument.Parse(JsonSerializer.Serialize(new { path }));
        await foreach (var _ in new FileReadTool().ExecuteAsync(readDoc.RootElement, ctx, default))
        {
        }

        Assert.False(ctx.Reads!.WasRead(path));
    }

    // 방금 Write 로 만든 파일은 Read 없이 바로 Edit·재Write 할 수 있다(쓴 내용은 모델이 안다).
    [Fact]
    public async Task Edit_and_rewrite_right_after_write_need_no_read()
    {
        var ctx = new ToolContext(_dir, PermissionMode.Auto, new ReadTracker());

        async Task<bool> RunWith(ITool tool, string json)
        {
            using var doc = JsonDocument.Parse(json);
            var err = false;
            await foreach (var p in tool.ExecuteAsync(doc.RootElement, ctx, default))
            {
                err |= p is ToolOutput { IsError: true };
            }

            return err;
        }

        Assert.False(await RunWith(new FileWriteTool(), """{"path":"w.txt","content":"alpha beta\n"}"""));
        Assert.False(await RunWith(new FileEditTool(), """{"path":"w.txt","old_string":"beta","new_string":"gamma"}"""));
        Assert.Equal("alpha gamma\n", File.ReadAllText(Path.Combine(_dir, "w.txt")));
        Assert.False(await RunWith(new FileWriteTool(), """{"path":"w.txt","content":"again\n"}"""));

        // 다른 사람이 만든(읽지 않은) 기존 파일은 여전히 Read 가 먼저다.
        File.WriteAllText(Path.Combine(_dir, "other.txt"), "x");
        Assert.True(await RunWith(new FileEditTool(), """{"path":"other.txt","old_string":"x","new_string":"y"}"""));
    }

    [Fact]
    public async Task Write_then_Read_roundtrips()
    {
        var (_, werr) = await Run(new FileWriteTool(),
            """{"path":"a.txt","content":"line1\nline2\n"}""");
        Assert.False(werr);

        var (read, rerr) = await Run(new FileReadTool(), """{"path":"a.txt"}""");
        Assert.False(rerr);
        Assert.Contains("line1", read);
        Assert.Contains("line2", read);
        Assert.Contains("1\t", read); // 라인 번호 부여
    }

    [Fact]
    public async Task Edit_replaces_unique_string()
    {
        await Run(new FileWriteTool(), """{"path":"b.txt","content":"hello world"}""");

        var (_, err) = await Run(new FileEditTool(),
            """{"path":"b.txt","old_string":"world","new_string":"openclaude"}""");
        Assert.False(err);

        var (read, _) = await Run(new FileReadTool(), """{"path":"b.txt"}""");
        Assert.Contains("hello openclaude", read);
    }

    [Fact]
    public async Task Edit_fails_on_nonunique_without_replace_all()
    {
        await Run(new FileWriteTool(), """{"path":"c.txt","content":"x x x"}""");

        var (_, err) = await Run(new FileEditTool(),
            """{"path":"c.txt","old_string":"x","new_string":"y"}""");
        Assert.True(err);
    }

    [Fact]
    public async Task Edit_crlf_file_matches_lf_old_string()
    {
        // Windows CRLF 파일 폴백: Read 는 \r 을 떼어 보여주므로 모델의 old_string 은 LF-only.
        // 실사고: CRLF 소스에서 Edit 이 "not found" 연발 → 모델이 턴을 낭비하다 멈춤.
        await Run(new FileWriteTool(), """{"path":"win.cs","content":"alpha\r\nbeta\r\ngamma"}""");

        var (_, err) = await Run(new FileEditTool(),
            """{"path":"win.cs","old_string":"beta\r\ngamma","new_string":"BETA\r\nGAMMA"}""");
        // 모델이 LF-only 로 보내는 경우(JSON \n)도 동일하게 매칭돼야 한다.
        Assert.False(err);

        var (read, _) = await Run(new FileReadTool(), """{"path":"win.cs"}""");
        Assert.Contains("BETA", read);
        Assert.Contains("GAMMA", read);

        // LF-only old_string 도 CRLF 파일에 매칭(핵심 회귀).
        var (_, err2) = await Run(new FileEditTool(),
            """{"path":"win.cs","old_string":"alpha\nBETA","new_string":"ALPHA\nBETA"}""");
        Assert.False(err2);
        var (read2, _) = await Run(new FileReadTool(), """{"path":"win.cs"}""");
        Assert.Contains("ALPHA", read2);
    }

    [Fact]
    public async Task Glob_finds_by_pattern()
    {
        await Run(new FileWriteTool(), """{"path":"src/main.cs","content":"//"}""");
        await Run(new FileWriteTool(), """{"path":"src/util.cs","content":"//"}""");
        await Run(new FileWriteTool(), """{"path":"readme.md","content":"#"}""");

        var (output, err) = await Run(new GlobTool(), """{"pattern":"**/*.cs"}""");
        Assert.False(err);
        Assert.Contains("src/main.cs", output);
        Assert.Contains("src/util.cs", output);
        Assert.DoesNotContain("readme.md", output);
    }

    [Fact]
    public async Task Grep_finds_matching_lines()
    {
        await Run(new FileWriteTool(),
            """{"path":"code.txt","content":"alpha\nTODO: fix\nbeta\n"}""");

        var (output, err) = await Run(new GrepTool(), """{"pattern":"TODO"}""");
        Assert.False(err);
        Assert.Contains("code.txt:2:", output);
        Assert.Contains("TODO", output);
    }

    [Fact]
    public async Task Read_expands_leading_tilde_to_home()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var rel = ".moai-tilde-" + Guid.NewGuid().ToString("n") + ".txt";
        var abs = Path.Combine(home, rel);
        await File.WriteAllTextAsync(abs, "tilde-ok");
        try
        {
            // cwd(_dir)와 무관하게 ~/ 는 홈으로 해석돼야 한다.
            var (output, err) = await Run(new FileReadTool(),
                JsonSerializer.Serialize(new { path = "~/" + rel }));
            Assert.False(err);
            Assert.Contains("tilde-ok", output);
        }
        finally
        {
            File.Delete(abs);
        }
    }

    [Fact]
    public async Task Grep_clips_long_lines_to_bound_context()
    {
        var longLine = "MATCH " + new string('x', 5000);
        await Run(new FileWriteTool(),
            JsonSerializer.Serialize(new { path = "big.txt", content = longLine + "\n" }));

        var (output, err) = await Run(new GrepTool(), """{"pattern":"MATCH"}""");
        Assert.False(err);
        Assert.Contains("big.txt:1:", output);
        Assert.Contains("(+", output);             // 라인이 잘렸다는 표식
        Assert.True(output.Length < 1000);         // 5000자 한 줄이 그대로 들어오지 않음
    }
}

public class FileWalkerTests : IDisposable
{
    private readonly string _dir;

    public FileWalkerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "occw-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // 부모를 가리키는 순환 심볼릭 링크가 있어도 무한 루프 없이 종료해야 한다.
    [Fact]
    public void Walk_does_not_loop_on_cyclic_symlink()
    {
        var sub = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "a.txt"), "x");

        try
        {
            // sub/loop -> _dir (부모) : 따라가면 무한 순환
            Directory.CreateSymbolicLink(Path.Combine(sub, "loop"), _dir);
        }
        catch
        {
            return; // 심볼릭 링크 생성 불가 환경이면 스킵
        }

        var walker = new MoaiCode.Tools.FileWalker(TimeSpan.FromSeconds(5));
        var files = walker.Walk(_dir, CancellationToken.None).ToList();

        // 무한 루프였다면 여기 도달 못 함. 실제 파일은 보이되, 같은 파일이 수없이 중복되지 않아야.
        Assert.Contains(files, f => f.EndsWith("a.txt"));
        Assert.True(files.Count < 100, $"순환 링크를 따라가 파일이 폭증함: {files.Count}");
    }

    [Fact]
    public void Walk_skips_build_dirs()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "node_modules"));
        File.WriteAllText(Path.Combine(_dir, "node_modules", "dep.js"), "x");
        File.WriteAllText(Path.Combine(_dir, "real.cs"), "x");

        var walker = new MoaiCode.Tools.FileWalker(TimeSpan.FromSeconds(5));
        var files = walker.Walk(_dir, CancellationToken.None).ToList();

        Assert.Contains(files, f => f.EndsWith("real.cs"));
        Assert.DoesNotContain(files, f => f.EndsWith("dep.js"));
    }
}

public class PathSafetyTests
{
    [Fact]
    public void Denies_system_critical_paths()
    {
        if (OperatingSystem.IsWindows())
        {
            var win = Environment.GetFolderPath(Environment.SpecialFolder.System);
            Assert.NotNull(MoaiCode.Tools.PathSafety.DenyWriteReason(Path.Combine(win, "drivers", "x.sys")));
        }
        else
        {
            Assert.NotNull(MoaiCode.Tools.PathSafety.DenyWriteReason("/boot/grub/grub.cfg"));
            Assert.NotNull(MoaiCode.Tools.PathSafety.DenyWriteReason("/etc/passwd"));
            Assert.NotNull(MoaiCode.Tools.PathSafety.DenyWriteReason("/usr/bin/python"));
            Assert.NotNull(MoaiCode.Tools.PathSafety.DenyWriteReason("/"));
        }
    }

    [Fact]
    public void Allows_normal_project_paths()
    {
        var p = Path.Combine(Path.GetTempPath(), "proj", "src", "a.cs");
        Assert.Null(MoaiCode.Tools.PathSafety.DenyWriteReason(p));
    }

    [Fact]
    public void Outside_workspace_detection()
    {
        var ws = Path.Combine(Path.GetTempPath(), "wsproj");
        // 내부(상대/하위) = 안
        Assert.False(MoaiCode.Tools.PathSafety.IsOutsideWorkspace(ws, "src/a.cs"));
        Assert.False(MoaiCode.Tools.PathSafety.IsOutsideWorkspace(ws, Path.Combine(ws, "deep", "b.cs")));
        // 밖(절대/상위탈출/홈) = 밖
        Assert.True(MoaiCode.Tools.PathSafety.IsOutsideWorkspace(ws, "/etc/hosts"));
        Assert.True(MoaiCode.Tools.PathSafety.IsOutsideWorkspace(ws, "../sibling/c.cs"));
        Assert.True(MoaiCode.Tools.PathSafety.IsOutsideWorkspace(ws, "~/secret.txt"));
    }
}

public class WebFetchToolTests
{
    private static async Task<(string Text, bool Err)> Run(string json)
    {
        var tool = new MoaiCode.Tools.Web.WebFetchTool();
        var input = JsonDocument.Parse(json).RootElement;
        var ctx = new ToolContext(Path.GetTempPath(), PermissionMode.Auto);
        var sb = new StringBuilder();
        var err = false;
        await foreach (var p in tool.ExecuteAsync(input, ctx, CancellationToken.None))
        {
            if (p is ToolOutput o) { sb.Append(o.Text); err |= o.IsError; }
        }

        return (sb.ToString(), err);
    }

    [Fact]
    public void Html_to_text_strips_tags_scripts_styles()
    {
        var html = "<html><head><style>x{}</style><script>evil()</script></head>" +
                   "<body><h1>Title</h1><p>Hello <b>world</b></p><!-- c --></body></html>";
        var text = MoaiCode.Tools.Web.WebFetchTool.HtmlToText(html);
        Assert.Contains("Title", text);
        Assert.Contains("Hello", text);
        Assert.Contains("world", text);
        Assert.DoesNotContain("evil()", text);
        Assert.DoesNotContain("<", text);
    }

    [Fact]
    public async Task Rejects_non_http_scheme()
    {
        var (text, err) = await Run("""{"url":"file:///etc/passwd"}""");
        Assert.True(err);
        Assert.Contains("http(s)", text);
    }

    [Fact]
    public async Task Blocks_cloud_metadata_ip()
    {
        var (text, err) = await Run("""{"url":"http://169.254.169.254/latest/meta-data/"}""");
        Assert.True(err);
        Assert.Contains("169.254.169.254", text); // 차단된 메타데이터 IP 가 메시지에 그대로 노출(언어무관)
    }
}

public class ToolDisplayTests
{
    private static string D(string tool, string json)
        => MoaiCode.Tui.ToolDisplay.Describe(tool, JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Bash_shows_terminal_command_not_json()
    {
        var s = D("Bash", """{"command":"ssh moai-ec2 'hostname'","timeout_ms":120000}""");
        Assert.Equal("$ ssh moai-ec2 'hostname'", s);
        Assert.DoesNotContain("timeout_ms", s);
        Assert.DoesNotContain("{", s);
    }

    [Fact]
    public void File_tools_show_path()
    {
        Assert.Equal("Read /a/b.cs", D("Read", """{"path":"/a/b.cs"}"""));
        Assert.Equal("Edit /a/b.cs", D("Edit", """{"path":"/a/b.cs"}"""));
    }

    [Fact]
    public void Grep_shows_pattern()
        => Assert.Contains("Grep \"TODO\"", D("Grep", """{"pattern":"TODO","path":"src"}"""));

    [Fact]
    public void Unknown_tool_pretty_json()
    {
        var s = D("mcp__x__y", """{"a":1}""");
        Assert.Contains("\"a\"", s); // JSON 폴백 (들여쓰기)
    }

    [Fact]
    public void Task_tools_are_readable_not_json()
    {
        Assert.Equal("TaskList", D("TaskList", "{}"));
        Assert.Equal("TaskCreate: fix bug", D("TaskCreate", """{"subject":"fix bug"}"""));
        Assert.Equal("TaskUpdate #4 → completed", D("TaskUpdate", """{"id":"4","status":"completed"}"""));
    }
}

public class BashSecurityHardeningTests
{
    [Theory]
    [InlineData("rm -rf /boot")]
    [InlineData("rm -rf /etc")]
    [InlineData("rm -rf /usr/lib")]
    [InlineData("rm -rf /*")]
    [InlineData("sudo rm -rf /")]
    [InlineData("format c:")]
    [InlineData("rd /s /q C:\\")]
    [InlineData("rmdir /s /q C:\\Windows")]
    [InlineData("diskpart")]
    [InlineData("bcdedit /delete {current}")]
    [InlineData("diskutil eraseDisk JHFS+ X /dev/disk2")]
    public void Blocks_destructive(string cmd)
        => Assert.False(MoaiCode.Tools.Bash.BashSecurity.Check(cmd).Allowed, cmd);

    [Theory]
    [InlineData("rm -rf ./build")]
    [InlineData("rm -f tmp.txt")]
    [InlineData("ls -la /etc")]          // 읽기는 허용
    [InlineData("cat /etc/hosts")]
    [InlineData("npm run build")]
    public void Allows_safe(string cmd)
        => Assert.True(MoaiCode.Tools.Bash.BashSecurity.Check(cmd).Allowed, cmd);

    // 원격 실행/전송은 '확인 필요'(차단 아님)로 판정 — auto 모드여도 게이트로 보냄.
    [Theory]
    [InlineData("ssh moai-ec2 'ls -la'")]
    [InlineData("scp file.txt host:/tmp/")]
    [InlineData("rsync -a ./ user@host:/srv/")]
    [InlineData("kubectl get pods")]
    [InlineData("docker -H tcp://1.2.3.4:2375 ps")]
    public void NeedsConfirmation_for_remote(string cmd)
        => Assert.True(MoaiCode.Tools.Bash.BashSecurity.NeedsConfirmation(cmd), cmd);

    [Theory]
    [InlineData("ls -la")]
    [InlineData("docker ps")]
    [InlineData("rsync -a ./a ./b")]     // 로컬 rsync(원격 host: 없음)는 확인 불필요
    public void No_confirmation_for_local(string cmd)
        => Assert.False(MoaiCode.Tools.Bash.BashSecurity.NeedsConfirmation(cmd), cmd);
}
