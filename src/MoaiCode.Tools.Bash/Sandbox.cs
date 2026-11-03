using System.Diagnostics;
using System.Runtime.InteropServices;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.Bash;

/// <summary>
/// bubblewrap(bwrap) 로 셸 명령을 격리한다(<see cref="SandboxPolicy"/>). 격리 안에서는
/// <list type="bullet">
/// <item>파일 시스템 전체가 읽기 전용이고, 작업 폴더·설정한 쓰기 경로·이 세션 전용 /tmp 만 쓸 수 있다.</item>
/// <item>/dev 는 최소 장치만(null·zero·random·tty) — 디스크 장치가 없다. /proc 는 새로 띄운 것이고 /proc/sysrq-trigger 는 막는다.</item>
/// <item>/run 을 비운다 — docker.sock·systemd·D-Bus·사용자 런타임 소켓으로 호스트를 조작할 수 없다(DNS 설정만 남긴다).</item>
/// <item>PID·IPC·UTS·사용자 네임스페이스를 따로 쓴다 — 호스트 프로세스가 보이지 않아 죽일 수 없다. 새 세션이라 터미널 주입도 막힌다.</item>
/// <item>네트워크는 공유한다(kubectl get·psql 조회·내부 API 확인 같은 진단을 자동으로 할 수 있게). 원격 변경은 명령 판정이 사람에게 묻는다.</item>
/// </list>
/// </summary>
public static class Sandbox
{
    private const string Bwrap = "bwrap";

    /// <summary>이 머신에서 격리를 쓸 수 있는지 시험 실행한다(Linux + bwrap + 사용자 네임스페이스). 약 1초.</summary>
    public static bool Probe()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || FindBwrap() is not { } bwrap)
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo(bwrap)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in new[] { "--ro-bind", "/", "/", "--dev", "/dev", "--proc", "/proc", "--tmpfs", "/run",
                         "--unshare-user", "--unshare-pid", "--unshare-ipc", "--die-with-parent", "--new-session", "--", "/bin/sh", "-c", "exit 0" })
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }

            if (!p.WaitForExit(5000))
            {
                p.Kill(entireProcessTree: true);
                return false;
            }

            return p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>(셸, 인자)를 격리 실행 (bwrap, 인자)로 감싼다.</summary>
    public static (string Exe, string[] Args) Wrap(string shell, string[] args, string workDir)
    {
        var a = new List<string>
        {
            "--ro-bind", "/", "/",
            "--dev", "/dev",
            "--proc", "/proc",
            "--ro-bind", "/dev/null", "/proc/sysrq-trigger",
            "--tmpfs", "/run",
            "--ro-bind-try", "/run/systemd/resolve", "/run/systemd/resolve",   // resolv.conf 가 여기로 링크된 배포판(DNS)
        };

        var tmp = SandboxPolicy.TmpDir;
        if (tmp is not null && Directory.Exists(tmp))
        {
            a.AddRange(new[] { "--bind", tmp, "/tmp", "--bind", tmp, "/var/tmp" });
        }
        else
        {
            a.AddRange(new[] { "--tmpfs", "/tmp", "--tmpfs", "/var/tmp" });
        }

        foreach (var w in SandboxPolicy.Writable)
        {
            if (Directory.Exists(w))
            {
                a.AddRange(new[] { "--bind", w, w });
            }
        }

        a.AddRange(new[]
        {
            "--unshare-user", "--unshare-pid", "--unshare-ipc", "--unshare-uts", "--unshare-cgroup-try",
            "--die-with-parent", "--new-session",
            "--setenv", "TMPDIR", "/tmp",
            "--setenv", "MATE_SANDBOX", "1",
            "--chdir", Directory.Exists(workDir) ? workDir : "/",
            "--", shell,
        });
        a.AddRange(args);
        return (FindBwrap() ?? Bwrap, a.ToArray());
    }

    /// <summary>격리 때문에 실패했을 법한 출력이면 모델에게 줄 안내. 아니면 null.</summary>
    public static bool LooksBlockedBySandbox(string output) =>
        output.Contains("Read-only file system", StringComparison.OrdinalIgnoreCase)
        || output.Contains("읽기전용 파일 시스템", StringComparison.Ordinal)
        || output.Contains("읽기 전용 파일 시스템", StringComparison.Ordinal)
        || output.Contains("docker.sock", StringComparison.Ordinal)
        || output.Contains("Failed to connect to bus", StringComparison.Ordinal);

    private static string? FindBwrap()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':'))
        {
            var p = Path.Combine(dir, Bwrap);
            if (File.Exists(p))
            {
                return p;
            }
        }

        return null;
    }
}
