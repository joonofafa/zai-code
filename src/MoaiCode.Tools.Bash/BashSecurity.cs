using System.Linq;
using System.Text.RegularExpressions;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

namespace MoaiCode.Tools.Bash;

/// <summary>
/// 셸 명령 보안 판정. 세 단계(2026-11-03 Bill 결정):
/// <list type="number">
/// <item><see cref="Check"/> — 되돌릴 수 없는 명령(rm -rf / 와 시스템 트리, mkfs, 디스크 덮어쓰기, 전원, 내려받은 스크립트 실행)은
/// 무조건 차단.</item>
/// <item><see cref="DestructiveReason"/> — 파괴적이지만 정당할 수 있는 명령. 권한 모드·allow 규칙과 무관하게 사람이 반드시 확인한다
/// (배치 자동화는 --ignore-confirm + 감사 로그로만 통과). 무엇이 실행될지 알 수 없는 형태(변수로 된 프로그램, eval, 표준 입력을
/// 프로그램으로 읽는 셸)도 여기 든다 — 모르면 묻는다.</item>
/// <item><see cref="RemoteReason"/> — 원격 실행·전송·클러스터 접속. 확인을 받되 신뢰한 대상은 allow 규칙으로 건너뛸 수 있다.</item>
/// </list>
/// 명령 해석은 <see cref="ShellCommand.Parse"/>(따옴표·치환·래퍼·heredoc). 따옴표 안의 글자는 명령이 아니다 — 실행되는 문자열만 다시 본다.
/// 정규식은 SQL 본문과 프로그램 코드 같은 '내용'에만 쓴다.
/// </summary>
public static class BashSecurity
{
    public sealed record Verdict(bool Allowed, string? Reason);

    /// <summary>내용의 종류 — 실행할 명령(셸 한 줄), 셸 스크립트 파일, SQL, 프로그램 코드.</summary>
    public enum Content { Command, ShellScript, Sql, Code }

    /// <summary>읽기 전용으로 간주되는 안전한 명령 prefix (권한 자동 허용 후보).
    /// find(-delete/-exec 가능), env(임의 명령 실행 가능)는 의도적으로 제외한다.</summary>
    private static readonly HashSet<string> ReadOnlyCommands = new(StringComparer.Ordinal)
    {
        "ls", "cat", "pwd", "echo", "grep", "rg", "head", "tail",
        "wc", "stat", "file", "which", "whoami", "date", "tree", "du", "df",
    };

    /// <summary>SQL 을 실행하는 클라이언트 — 이 명령에 실리는 문자열만 SQL 로 본다(`grep "DROP TABLE"` 은 SQL 이 아니다).</summary>
    public static readonly IReadOnlySet<string> DbClients = new HashSet<string>(StringComparer.Ordinal)
    {
        "psql", "mysql", "mariadb", "sqlplus", "sqlcmd", "osql", "redis-cli", "mongo", "mongosh", "clickhouse-client",
        "clickhouse", "cqlsh", "isql", "tbsql", "db2", "beeline", "hive", "impala-shell", "spark-sql", "sqlite3", "duckdb",
        "trino", "presto", "cockroach", "mysqlsh",
    };

    // ---------- 내용 정규식(SQL·코드) ----------

    private static readonly (Regex Pattern, string ReasonKey)[] SqlPatterns =
    {
        (Rx(@"\b(DROP|TRUNCATE)\s+(TABLE|DATABASE|SCHEMA|USER|ROLE|INDEX|VIEW|TABLESPACE|SEQUENCE|FUNCTION|PROCEDURE|TRIGGER|OWNED|EXTENSION|MATERIALIZED|PACKAGE|SYNONYM|PARTITION|COLLECTION|KEYSPACE)\b"), "tools.bashSecurity.dbDrop"),
        (Rx(@"\bTRUNCATE\s+(ONLY\s+)?[A-Za-z_""][\w."".]*"), "tools.bashSecurity.dbDrop"),
        (Rx(@"\bDELETE\s+FROM\b"), "tools.bashSecurity.dbChange"),
        (Rx(@"\bUPDATE\s+[A-Za-z_""][\w."".]*\s+SET\b"), "tools.bashSecurity.dbChange"),
        (Rx(@"\bALTER\s+(TABLE|DATABASE|SYSTEM|USER|ROLE|SCHEMA|TABLESPACE|SEQUENCE)\b"), "tools.bashSecurity.dbChange"),
        (Rx(@"\b(GRANT|REVOKE)\s+\w"), "tools.bashSecurity.dbChange"),
        (Rx(@"\b(VACUUM\s+FULL|REINDEX\s+(TABLE|INDEX|DATABASE|SCHEMA|SYSTEM)|pg_terminate_backend|pg_drop_replication_slot)\b"), "tools.bashSecurity.dbChange"),
        (Rx(@"\bSHUTDOWN\b"), "tools.bashSecurity.dbChange"),
        (Rx(@"\b(FLUSHALL|FLUSHDB)\b"), "tools.bashSecurity.dbDrop"),
        (Rx(@"\bdropDatabase\s*\(|\.drop\s*\(\s*\)|\.deleteMany\s*\(|\.remove\s*\(\s*\{\s*\}\s*\)"), "tools.bashSecurity.dbDrop"),
        (Rx(@"\\!"), "tools.bashSecurity.shellIndirection"),   // psql \! — SQL 클라이언트 안에서 셸 명령
    };

    private static readonly Regex DeleteApi = Rx(
        @"\b(shutil\.rmtree|os\.(remove|unlink|rmdir|removedirs|system)\s*\(|rmSync\s*\(|rimraf\b|fs\.(rm|unlink|rmdir)(Sync)?\s*\(|" +
        @"File\.Delete\s*\(|Directory\.Delete\s*\(|FileUtils\.(rm_rf|rm_r|rm|remove_dir|remove_entry)\b|\.unlink\s*\(|\.rmdir\s*\(|" +
        @"unlink\s*\(|rmtree\s*\(|os\.RemoveAll\s*\(|Files\.delete|system\s*\(?\s*[""']|subprocess\.|Runtime\.getRuntime)");

    // 명령 단어를 공백으로 이은 문자열에 대해 맨 앞에서만 맞춘다(따옴표 안 글자에 반응하지 않게).
    private static readonly (Regex Pattern, string ReasonKey)[] CommandPatterns =
    {
        (Rx(@"^aws\s+s3\s+(rb|rm|mv)\b"), "tools.bashSecurity.s3Delete"),
        (Rx(@"^aws\s+s3\s+sync\b.*\s--delete\b"), "tools.bashSecurity.s3Delete"),
        (Rx(@"^(aws|gcloud|az)\b.*\s(delete|terminate|destroy|remove|stop|deallocate)\b"), "tools.bashSecurity.cloudDelete"),
        (Rx(@"^npm\s+(install|i|add|update|up)\b.*\s(-g|--global)\b"), "tools.bashSecurity.npmGlobal"),
        (Rx(@"^pnpm\s+(add|install|update|up)\b.*\s(-g|--global)\b"), "tools.bashSecurity.pnpmGlobal"),
        (Rx(@"^yarn\s+global\s+(add|upgrade)\b"), "tools.bashSecurity.yarnGlobal"),
        (Rx(@"^dotnet\s+tool\s+(install|update)\b.*\s(-g|--global)\b"), "tools.bashSecurity.dotnetGlobalTool"),
        (Rx(@"^pipx\s+install\b"), "tools.bashSecurity.pipxGlobal"),
        (Rx(@"^pip3?\s+install\b.*\s--user\b"), "tools.bashSecurity.pipUser"),
        (Rx(@"^(cargo|gem)\s+install\b"), "tools.bashSecurity.cargoGemGlobal"),
        (Rx(@"^go\s+install\b"), "tools.bashSecurity.goInstall"),
        (Rx(@"^apt(-get)?\s+(install|remove|purge|upgrade|full-upgrade|autoremove|dist-upgrade)\b"), "tools.bashSecurity.aptChange"),
        (Rx(@"^(dnf|yum|zypper)\s+(install|remove|erase|update|upgrade|downgrade|autoremove|reinstall)\b"), "tools.bashSecurity.sysPkgChange"),
        (Rx(@"^rpm\s+(-\w*[eiUF]\w*|--erase|--install|--upgrade|--freshen)\b"), "tools.bashSecurity.sysPkgChange"),
        (Rx(@"^dpkg\s+(-\w*[irP]\w*|--install|--remove|--purge)\b"), "tools.bashSecurity.sysPkgChange"),
        (Rx(@"^pacman\s+-S"), "tools.bashSecurity.pacmanInstall"),
        (Rx(@"^apk\s+(add|del)\b"), "tools.bashSecurity.apkChange"),
        (Rx(@"^brew\s+(install|uninstall|upgrade)\b"), "tools.bashSecurity.brewChange"),
        (Rx(@"^snap\s+(install|remove)\b"), "tools.bashSecurity.snapChange"),
        (Rx(@"^(choco|winget|scoop)\s+(install|uninstall|upgrade)\b"), "tools.bashSecurity.winPkgChange"),
    };

    // Windows(cmd/PowerShell)·fork bomb — 원문에 맞춘다(이 셸 해석기는 cmd 문법을 모른다).
    private static readonly (Regex Pattern, string ReasonKey)[] RawHard =
    {
        (Rx(@":\s*\(\s*\)\s*\{\s*:\s*\|\s*:"), "tools.bashSecurity.forkBomb"),
        (Rx(@"\bformat\s+[a-z]:"), "tools.bashSecurity.formatDrive"),
        (Rx(@"\bdiskpart\b"), "tools.bashSecurity.diskpart"),
        (Rx(@"\bcipher\s+/w"), "tools.bashSecurity.cipherWipe"),
        (Rx(@"\bbcdedit\b.*\b(delete|deletevalue|/delete)\b"), "tools.bashSecurity.bcdeditDelete"),
        (Rx(@"\b(rd|rmdir)\s+(/[sq]\s+)+([a-z]:\\?(\s|$)|.*\\(Windows|System32|Boot|Program Files))"), "tools.bashSecurity.winRdSystem"),
        (Rx(@"\bdel\s+(/[a-z]\s+)*.*\\(Windows|System32|Boot)\b"), "tools.bashSecurity.winDelSystem"),
        (Rx(@"Remove-Item\b.*-Recurse\b.*-Force\b.*([a-z]:\\?\s|\\(Windows|System32|Program Files))"), "tools.bashSecurity.psForceRecursive"),
        (Rx(@"\b(diskutil)\s+(eraseDisk|eraseVolume|reformat|zeroDisk)\b"), "tools.bashSecurity.diskutilErase"),
    };

    private static readonly (Regex Pattern, string ReasonKey)[] WindowsConfirm =
    {
        (Rx(@"\b(del|erase)\b[^|;&]*\s/[a-z]*[sq]"), "tools.bashSecurity.winDelErase"),
        (Rx(@"\b(rd|rmdir)\b[^|;&]*\s/s\b"), "tools.bashSecurity.winRdRecursive"),
        (Rx(@"\bRemove-Item\b[^|;&]*\s-(Recurse|Force)\b"), "tools.bashSecurity.psRemoveItem"),
        (Rx(@"\b(ri|del)\b[^|;&]*\s-Recurse\b"), "tools.bashSecurity.psRecursiveDelete"),
    };

    private static readonly Regex DiskDevice = Rx(@"^/dev/(sd|nvme|disk|hd|rdisk|vd|xvd|mmcblk|dm-|md[0-9]|loop[0-9]|mapper/|nbd|rbd|zd|sr[0-9])");

    // ---------- 경로 ----------

    private static readonly string[] SystemPaths =
    {
        "/etc", "/boot", "/usr", "/bin", "/sbin", "/lib", "/lib64", "/var/lib", "/var/log", "/var/spool", "/opt", "/root", "/srv",
    };

    private static bool IsSystemPath(string arg)
    {
        if (!arg.StartsWith('/'))
        {
            return false;
        }

        var p = ShellCommand.NormalizePath(arg);
        return p == "/" || SystemPaths.Any(s => p == s || p.StartsWith(s + "/", StringComparison.Ordinal));
    }

    private static bool IsOutsideRelative(string arg) =>
        arg.StartsWith('/') || arg.StartsWith('~') || arg.StartsWith('$') || arg.Contains("..", StringComparison.Ordinal);

    // ---------- kubectl / helm ----------

    private static readonly HashSet<string> KubeValueFlags = new(StringComparer.Ordinal)
    {
        "-n", "--namespace", "--context", "--cluster", "--kubeconfig", "--config", "-l", "--selector", "-o", "--output",
        "-c", "--container", "-s", "--server", "--user", "--token", "--as", "--as-group", "-f", "--filename",
        "--request-timeout", "--field-selector", "--certificate-authority", "--client-certificate", "--client-key",
    };

    private static readonly HashSet<string> KubeReadVerbs = new(StringComparer.Ordinal)
    {
        "get", "describe", "logs", "top", "explain", "version", "api-resources", "api-versions", "cluster-info",
        "events", "diff", "wait", "kustomize", "completion", "whoami", "status", "projects", "options", "help",
    };

    private static readonly HashSet<string> KubeRemoteVerbs = new(StringComparer.Ordinal)
    {
        "exec", "rsh", "rsync", "attach", "port-forward", "proxy", "cp", "debug", "login", "logout", "project", "config", "plugin",
    };

    private static readonly HashSet<string> HelmReadVerbs = new(StringComparer.Ordinal)
    {
        "list", "ls", "status", "get", "history", "hist", "show", "inspect", "template", "search", "version",
        "env", "lint", "verify", "help", "completion",
    };

    private static readonly HashSet<string> HelmChangeVerbs = new(StringComparer.Ordinal)
    {
        "install", "upgrade", "uninstall", "delete", "del", "un", "rollback", "push",
    };

    private enum Cluster { Read, Remote, Change }

    private static string Prog(string word)
    {
        var p = ShellCommand.Program(word);
        return p switch
        {
            "microk8s.kubectl" => "kubectl",
            "microk8s.helm" or "microk8s.helm3" or "helm3" => "helm",
            _ => p,
        };
    }

    private static Cluster ClusterClass(List<string> words)
    {
        var at = ShellCommand.FirstOperand(words, 1, KubeValueFlags);
        if (at < 0)
        {
            return Cluster.Read;
        }

        var verb = words[at];
        var sub = words.Skip(at + 1).FirstOrDefault(a => !a.StartsWith('-'));
        if (Prog(words[0]) == "helm")
        {
            return HelmReadVerbs.Contains(verb) ? Cluster.Read
                : HelmChangeVerbs.Contains(verb) ? Cluster.Change
                : verb is "repo" && sub is "list" ? Cluster.Read
                : Cluster.Remote;
        }

        if (KubeReadVerbs.Contains(verb))
        {
            return Cluster.Read;
        }

        return verb switch
        {
            "auth" => sub is "can-i" or "whoami" ? Cluster.Read : Cluster.Change,
            "rollout" => sub is "status" or "history" ? Cluster.Read : Cluster.Change,
            "adm" => sub is "top" or "who-can" ? Cluster.Read : sub is "inspect" or "must-gather" ? Cluster.Remote : Cluster.Change,
            "config" => sub is "view" or "get-contexts" or "current-context" or "get-clusters" or "get-users" ? Cluster.Read : Cluster.Remote,
            _ when KubeRemoteVerbs.Contains(verb) => Cluster.Remote,
            _ => Cluster.Change,
        };
    }

    // ---------- 공개 판정 ----------

    /// <summary>
    /// 파괴적 명령이면 그 사유 — 권한 모드·allow 규칙과 무관하게 사람의 확인이 필요하다. 해당 없으면 null.
    /// <paramref name="kind"/> 가 스크립트·SQL·코드이면 파일 내용을 검사한다(해석할 수 없는 형태는 내용 검사에선 묻지 않는다 —
    /// 실제 스크립트는 변수로 프로그램을 부르는 일이 흔하다).
    /// </summary>
    public static string? DestructiveReason(string command, Content kind = Content.Command)
    {
        var text = command ?? string.Empty;
        if (text.Trim().Length == 0)
        {
            return null;
        }

        switch (kind)
        {
            case Content.Sql:
                return SqlReason(StripSqlComments(text));
            case Content.Code:
                return CodeReason(text);
        }

        if (Rm.NeedsConfirmation(text))
        {
            return L10n.Get(Rm.IsRecursiveDelete(text) ? "tools.bashSecurity.recursiveDelete" : "tools.bashSecurity.riskyDelete");
        }

        var sql = new List<string>();
        foreach (var p in ShellCommand.Parse(text))
        {
            if (p.Indirect is not null && kind == Content.Command && !p.CodeFromString)
            {
                return L10n.Get("tools.bashSecurity.shellIndirection");
            }

            if (p.Words.Count > 0 && p.Words[0] == "(code)")
            {
                if (CodeReason(p.Words[1]) is { } codeReason)
                {
                    return codeReason;
                }

                continue;
            }

            foreach (var r in p.Redirects)
            {
                if (IsWriteRedirect(r.Op) && IsSystemPath(r.Target))
                {
                    return L10n.Get("tools.bashSecurity.systemFileChange");
                }
            }

            if (p.Words.Count == 0)
            {
                continue;
            }

            if (ProgramReason(p.Words) is { } key)
            {
                return L10n.Get(key);
            }

            var joined = Prog(p.Words[0]) + (p.Words.Count > 1 ? " " + string.Join(' ', p.Words.Skip(1)) : string.Empty);
            foreach (var (pattern, reasonKey) in CommandPatterns)
            {
                if (pattern.IsMatch(joined))
                {
                    return L10n.Get(reasonKey);
                }
            }

            if (DbClients.Contains(Prog(p.Words[0])))
            {
                sql.AddRange(SqlParts(p));
                if (p.FromPipe)
                {
                    sql.Add(text);   // echo "DROP TABLE t" | psql — 앞 명령이 SQL 을 만든다
                }
            }
        }

        if (sql.Count > 0 && SqlReason(StripSqlComments(string.Join('\n', sql))) is { } sqlReason)
        {
            return sqlReason;
        }

        foreach (var (pattern, reasonKey) in WindowsConfirm)
        {
            if (pattern.IsMatch(text))
            {
                return L10n.Get(reasonKey);
            }
        }

        return null;
    }

    /// <summary>원격 실행·전송·클러스터 접속이면 그 사유(확인 대상, allow 규칙으로 건너뛸 수 있음). 아니면 null.</summary>
    public static string? RemoteReason(string command)
    {
        var cmd = command?.Trim() ?? string.Empty;
        if (cmd.Length == 0)
        {
            return null;
        }

        if (cmd.Contains("DOCKER_HOST=", StringComparison.OrdinalIgnoreCase))
        {
            return L10n.Get("tools.bashSecurity.remoteExec");
        }

        foreach (var p in ShellCommand.Parse(cmd))
        {
            if (p.Wrappers.Any(w => w is "ssh" or "mosh"))
            {
                return L10n.Get("tools.bashSecurity.remoteExec");
            }

            if (p.Wrappers.Any(w => w is "kubectl" or "oc"))
            {
                return L10n.Get("tools.bashSecurity.clusterAccess");
            }

            if (p.Words.Count == 0)
            {
                continue;
            }

            var prog = Prog(p.Words[0]);
            var args = p.Words.Skip(1).ToList();
            switch (prog)
            {
                case "ssh" or "scp" or "sftp" or "telnet" or "mosh":
                case "rsync" when args.Any(a => !a.StartsWith('-') && a.Contains(':')):
                case "docker" or "podman" when args.Any(a => a is "-H" or "--host" || a.StartsWith("--host=", StringComparison.Ordinal)):
                    return L10n.Get("tools.bashSecurity.remoteExec");
                case "kubectl" or "oc" or "helm" when ClusterClass(p.Words) != Cluster.Read:
                    return L10n.Get("tools.bashSecurity.clusterAccess");
            }
        }

        return null;
    }

    /// <summary>'항상 확인' 대상이면 true — 파괴적(<see cref="DestructiveReason"/>) 또는 원격(<see cref="RemoteReason"/>).</summary>
    public static bool NeedsConfirmation(string command) => ConfirmationReason(command) is not null;

    /// <summary>'확인' 사유(표시용). 파괴적 사유가 원격 사유보다 앞선다. 해당 없으면 null.</summary>
    public static string? ConfirmationReason(string command) =>
        DestructiveReason(command) ?? RemoteReason(command);

    /// <summary>되돌릴 수 없는 명령이면 차단(Allowed=false). 스크립트 내용에도 쓴다.</summary>
    public static Verdict Check(string command)
    {
        var cmd = command?.Trim() ?? string.Empty;
        if (cmd.Length == 0)
        {
            return new Verdict(false, L10n.Get("tools.bashSecurity.emptyCommand"));
        }

        if (Rm.DisablesPreserveRoot(cmd))
        {
            return Blocked("tools.bashSecurity.rmNoPreserveRoot");
        }

        if (Rm.IsCriticalDelete(cmd))
        {
            return Blocked("tools.bashSecurity.criticalDelete");
        }

        var parsed = ShellCommand.Parse(cmd);
        var downloads = parsed.Any(p => p.Words.Count > 0 && Prog(p.Words[0]) is "curl" or "wget");
        foreach (var p in parsed)
        {
            // 내려받은 내용을 셸로: curl … | sh, bash <(curl …), sh -c "$(curl …)", x=$(curl …); $x
            if (downloads && p.Indirect is "program-from-input" or "variable-program")
            {
                return Blocked("tools.bashSecurity.remoteScriptExec");
            }

            foreach (var r in p.Redirects)
            {
                if (!IsWriteRedirect(r.Op))
                {
                    continue;
                }

                if (ShellCommand.NormalizePath(r.Target) == "/proc/sysrq-trigger")
                {
                    return Blocked("tools.bashSecurity.powerCommand");
                }

                if (DiskDevice.IsMatch(r.Target))
                {
                    return Blocked("tools.bashSecurity.redirectBlockDevice");
                }
            }

            if (p.Words.Count > 0 && p.Words[0] != "(code)" && HardBlockKey(p.Words) is { } key)
            {
                return Blocked(key);
            }
        }

        foreach (var (pattern, reasonKey) in RawHard)
        {
            if (pattern.IsMatch(cmd))
            {
                return Blocked(reasonKey);
            }
        }

        return new Verdict(true, null);
    }

    private static Verdict Blocked(string key) =>
        new(false, L10n.Get("tools.bashSecurity.blockedFmt", L10n.Get(key)));

    private static bool IsWriteRedirect(string op) => op is ">" or ">>" or ">|" or "&>" or "&>>" or "<>";

    private static string? HardBlockKey(List<string> words)
    {
        var p = Prog(words[0]);
        var args = words.Skip(1).ToList();
        var first = args.FirstOrDefault(a => !a.StartsWith('-'));
        bool Has(params string[] xs) => args.Any(a => xs.Contains(a, StringComparer.Ordinal));

        if (p.StartsWith("mkfs", StringComparison.Ordinal) || p is "mke2fs" or "mkswap" or "mkdosfs" or "mkntfs")
        {
            return "tools.bashSecurity.mkfs";
        }

        if (p is "shutdown" or "reboot" or "halt" or "poweroff"
            || (p is "systemctl" or "loginctl" && Has("reboot", "poweroff", "halt", "kexec", "soft-reboot"))
            || (p is "init" or "telinit" && first is "0" or "6"))
        {
            return "tools.bashSecurity.powerCommand";
        }

        if (p == "nvme" && first is "format" or "sanitize" or "delete-ns")
        {
            return "tools.bashSecurity.mkfs";
        }

        if ((p == "dd" && args.Any(a => a.StartsWith("of=", StringComparison.Ordinal) && DiskDevice.IsMatch(a[3..])))
            || (p is "cp" or "tee" or "pv" or "shred" or "wipefs" or "blkdiscard" && args.Any(a => !a.StartsWith('-') && DiskDevice.IsMatch(a))))
        {
            return "tools.bashSecurity.ddBlockDevice";
        }

        if (p == "chmod" && args.Any(a => a.TrimStart('0') == "777") && args.Any(a => a.StartsWith('-') && a.Contains('R'))
            && args.Any(a => a.StartsWith('/') && ShellCommand.NormalizePath(a) == "/"))
        {
            return "tools.bashSecurity.chmodRoot777";
        }

        if (p == "find")
        {
            var root = args.FirstOrDefault();
            var exec = args.SkipWhile(a => a is not ("-exec" or "-execdir" or "-ok" or "-okdir")).Skip(1).FirstOrDefault();
            if (root is not null && (root is "~" or "$HOME" || (root.StartsWith('/') && ShellCommand.NormalizePath(root) == "/"))
                && (Has("-delete") || (exec is not null && ShellCommand.Program(exec) is "rm" or "unlink" or "shred")))
            {
                return "tools.bashSecurity.findDeleteFromRoot";
            }
        }

        if (p == "git" && GitSub(args) is ("push", var rest)
            && rest.Any(a => a is "--force" or "-f" || a.StartsWith("--force", StringComparison.Ordinal) || a.StartsWith('+'))
            && rest.Any(a => a.TrimStart('+').Split(':').Last() is "main" or "master"))
        {
            return "tools.bashSecurity.forcePushProtected";
        }

        return null;
    }

    // 프로그램 이름(+인자)으로 판정하는 파괴적 명령. 명령 위치에서만 본다 — `grep reboot` 나 `man kill` 은 해당 없음.
    private static string? ProgramReason(List<string> words)
    {
        var p = Prog(words[0]);
        var args = words.Skip(1).ToList();
        bool Has(params string[] xs) => args.Any(a => xs.Contains(a, StringComparer.Ordinal));
        bool AnyFlag(char c) => args.Any(a => a.StartsWith('-') && !a.StartsWith("--", StringComparison.Ordinal) && a.Contains(c));
        var operands = args.Where(a => !a.StartsWith('-')).ToList();

        switch (p)
        {
            case "rm" or "rmdir" or "unlink" or "shred" when operands.Count == 0:
                return "tools.bashSecurity.stdinDelete";   // 대상 없는 삭제 = xargs 등 표준 입력으로 대상을 받는다
            case "kill" or "pkill" or "killall" or "skill" when !(p == "kill" && operands.Count > 0 && operands.All(a => a.StartsWith('%'))):
            case "fuser" when AnyFlag('k'):
                return "tools.bashSecurity.processKill";
            case "userdel" or "groupdel" or "usermod" or "useradd" or "groupadd" or "groupmod" or "passwd"
                or "chpasswd" or "chage" or "visudo" or "setenforce" or "deluser" or "adduser" or "gpasswd":
                return "tools.bashSecurity.accountChange";
            case "crontab" when !Has("-l"):
                return "tools.bashSecurity.cronChange";
            case "iptables" or "ip6tables" or "ebtables" or "arptables" when args.Count == 0 || args.Any(a => a.StartsWith('-') && !IsIptablesReadFlag(a)):
            case "iptables-restore" or "ip6tables-restore" or "ipset" when !Has("list", "-L", "-n", "--list"):
            case "nft" when operands.FirstOrDefault() is not ("list" or "monitor" or null):
            case "firewall-cmd" when args.Any(a => a.StartsWith("--", StringComparison.Ordinal)
                                             && !a.StartsWith("--list", StringComparison.Ordinal)
                                             && !a.StartsWith("--get", StringComparison.Ordinal)
                                             && !a.StartsWith("--query", StringComparison.Ordinal)
                                             && a is not ("--state" or "--zone" or "--permanent")
                                             && !a.StartsWith("--zone=", StringComparison.Ordinal)):
            case "ip" when Has("add", "del", "delete", "set", "flush", "change", "replace", "append"):
            case "ifdown" or "ifup":
            case "ifconfig" when Has("down", "up") || operands.Count >= 2:
            case "nmcli" when Has("down", "delete", "modify", "up", "reload"):
            case "route" when Has("add", "del", "delete"):
                return "tools.bashSecurity.networkChange";
            case "systemctl" when Has("stop", "disable", "mask", "restart", "kill", "isolate", "try-restart", "reload",
                "reload-or-restart", "try-reload-or-restart", "daemon-reexec", "set-default", "edit", "revert",
                "unmask", "enable", "start", "set-property", "default", "rescue", "emergency", "daemon-reload"):
            case "service" when Has("stop", "restart", "reload", "force-reload", "start"):
                return "tools.bashSecurity.serviceStop";
            case "sysctl" when AnyFlag('w') || AnyFlag('p') || Has("--write", "--load", "--system") || operands.Any(a => a.Contains('=')):
            case "modprobe" when !Has("-n", "--dry-run", "--show-depends", "-c", "--showconfig"):
            case "rmmod" or "insmod" or "kexec":
                return "tools.bashSecurity.kernelChange";
            case "umount" or "swapoff" or "wipefs" or "blkdiscard" or "lvremove" or "vgremove" or "pvremove" or "pvcreate" or "vgcreate"
                or "lvreduce" or "lvresize" or "vgreduce" or "lvchange" or "vgchange" or "chattr" or "losetup" or "zpool" or "zfs" or "shred":
            case "mdadm" when args.Any(a => a is "--stop" or "--remove" or "--fail" or "--zero-superblock" or "--create" or "-S" or "-r" or "-f"):
            case "cryptsetup" when Has("luksFormat", "erase", "luksErase", "luksKillSlot", "luksRemoveKey", "close"):
            case "parted" when !Has("-l", "--list", "print"):
            case "fdisk" or "sfdisk" or "sgdisk" or "gdisk" when !Has("-l", "--list", "-p", "--print", "-d", "--dump"):
            case "mount" when args.Any(a => a.Contains("remount", StringComparison.Ordinal)):
            case "dd" when args.Any(a => a.StartsWith("of=", StringComparison.Ordinal) && a != "of=/dev/null"):
                return "tools.bashSecurity.diskChange";
            case "mv" or "cp" or "install" or "ln" or "rsync" when operands.Any(IsSystemPath):
            case "tee" when operands.Any(IsSystemPath):
            case "sed" or "perl" when args.Any(a => a == "-i" || a.StartsWith("--in-place", StringComparison.Ordinal) || (a.StartsWith('-') && !a.StartsWith("--", StringComparison.Ordinal) && a.Contains('i')))
                                      && operands.Any(IsSystemPath):
            case "unlink" when operands.Any(IsSystemPath):
                return "tools.bashSecurity.systemFileChange";
            case "truncate":
                return "tools.bashSecurity.truncateEmpty";
            case "chmod" or "chown" or "chgrp" when operands.Any(IsSystemPath) || ((AnyFlag('R') || Has("--recursive")) && operands.Any(IsOutsideRelative)):
            case "setfacl" when AnyFlag('R') || AnyFlag('b') || Has("--recursive", "--remove-all"):
                return p == "chown" ? "tools.bashSecurity.chownRecursive" : "tools.bashSecurity.chmodRecursive";
            case "rsync" when args.Any(a => a.StartsWith("--delete", StringComparison.Ordinal) || a == "--remove-source-files"):
                return "tools.bashSecurity.syncDelete";
            case "journalctl" when args.Any(a => a.StartsWith("--vacuum", StringComparison.Ordinal) || a is "--rotate" or "--flush"):
                return "tools.bashSecurity.logTamper";
            case "curl" when CurlMethod(args) is "DELETE" or "PUT" or "PATCH":
            case "wget" when WgetMethod(args) is "DELETE" or "PUT" or "PATCH":
                return "tools.bashSecurity.httpMutation";
            case "find" when Has("-delete"):
                return "tools.bashSecurity.findDelete";
            case "git":
                return GitReason(args);
            case "terraform" or "tofu" when operands.FirstOrDefault() is "apply" or "destroy" or "import" or "taint" or "untaint" or "force-unlock"
                || (operands.FirstOrDefault() == "state" && Has("rm", "mv", "push", "replace-provider"))
                || (operands.FirstOrDefault() == "workspace" && Has("delete")):
                return operands.FirstOrDefault() == "destroy" || Has("-destroy") ? "tools.bashSecurity.terraformDestroy" : "tools.bashSecurity.iacApply";
            case "pulumi" when operands.FirstOrDefault() is "up" or "update" or "destroy" or "refresh" or "import" || (operands.FirstOrDefault() == "stack" && Has("rm")):
                return "tools.bashSecurity.iacApply";
            case "ansible-playbook" when !Has("--check", "-C", "--syntax-check", "--list-hosts", "--list-tasks"):
                return "tools.bashSecurity.fleetExec";
            case "ansible" when args.SkipWhile(a => a is not ("-m" or "--module-name")).Skip(1).FirstOrDefault() is not ("ping" or "setup" or "stat" or "debug" or "gather_facts"):
            case "pdsh" or "pssh" or "parallel-ssh" or "clush" or "salt" or "salt-ssh" or "mussh" or "dsh":
                return "tools.bashSecurity.fleetExec";
            case "docker" or "podman" or "nerdctl":
                return ContainerReason(args);
            case "docker-compose" or "podman-compose" when operands.FirstOrDefault() is "down" or "rm" or "stop" or "kill":
                return "tools.bashSecurity.dockerRemove";
            case "virsh" when operands.FirstOrDefault() is "destroy" or "undefine" or "shutdown" or "reboot" or "reset" or "vol-delete"
                or "vol-wipe" or "pool-destroy" or "pool-delete" or "pool-undefine" or "snapshot-delete" or "detach-disk"
                or "detach-interface" or "managedsave-remove" or "net-destroy" or "net-undefine" or "suspend" or "blockcommit":
                return "tools.bashSecurity.vmChange";
            case "kubectl" or "oc" or "helm" when ClusterClass(words) == Cluster.Change:
                return "tools.bashSecurity.clusterChange";
            case "pg_ctl" or "pg_ctlcluster" when Has("stop", "restart", "promote", "kill"):
            case "pg_restore" when !Has("-l", "--list"):
            case "dropdb" or "dropuser" or "pg_dropcluster" or "pg_resetwal" or "pg_resetxlog":
                return "tools.bashSecurity.dbChange";
        }

        // 일괄 파괴 하위 명령(zellij delete-all-sessions 등) — 하이픈·밑줄로 붙은 단어만(DeleteAll 같은 이름은 아님).
        if (operands.Any(a => BulkDelete.IsMatch(a)))
        {
            return "tools.bashSecurity.bulkDeleteSubcommand";
        }

        return null;
    }

    private static readonly Regex BulkDelete = Rx(@"^(delete|destroy|purge|prune|wipe|remove|kill)[-_]all([-_]\w+)?$");

    // curl 의 요청 메서드: -X M, -XM, -sXM(묶인 짧은 옵션), --request M, --request=M.
    private static string? CurlMethod(List<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "-X" or "--request")
            {
                return i + 1 < args.Count ? args[i + 1].ToUpperInvariant() : null;
            }

            if (a.StartsWith("--request=", StringComparison.Ordinal))
            {
                return a[10..].ToUpperInvariant();
            }

            if (a.StartsWith('-') && !a.StartsWith("--", StringComparison.Ordinal))
            {
                var x = a.IndexOf('X');
                if (x > 0)
                {
                    return x + 1 < a.Length ? a[(x + 1)..].ToUpperInvariant() : (i + 1 < args.Count ? args[i + 1].ToUpperInvariant() : null);
                }
            }
        }

        return null;
    }

    private static string? WgetMethod(List<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith("--method=", StringComparison.OrdinalIgnoreCase))
            {
                return args[i][9..].ToUpperInvariant();
            }

            if (args[i] == "--method" && i + 1 < args.Count)
            {
                return args[i + 1].ToUpperInvariant();
            }
        }

        return null;
    }

    // 조회 옵션만 있으면 읽기(-nvL, --list, -t nat -S). 하나라도 다른 옵션(-F, -A, -P, -Z …)이면 변경.
    private static bool IsIptablesReadFlag(string a) =>
        a.StartsWith("--", StringComparison.Ordinal)
            ? a is "--list" or "--list-rules" or "--numeric" or "--verbose" or "--exact" or "--line-numbers" or "--table" or "--wait"
                || a.StartsWith("--table=", StringComparison.Ordinal)
            : a.Length > 1 && a[1..].All(c => "LSnvxwt".Contains(c));

    private static (string Verb, List<string> Args)? GitSub(List<string> args)
    {
        var sub = ShellCommand.FirstOperand(args, 0, new HashSet<string>(StringComparer.Ordinal) { "-C", "-c", "--git-dir", "--work-tree", "--namespace", "--config-env" });
        return sub < 0 ? null : (args[sub], args.Skip(sub + 1).ToList());
    }

    private static string? GitReason(List<string> args)
    {
        // git -c alias.x='!sh …' / core.fsmonitor / core.sshCommand / core.hooksPath / *.pager … : 설정값이 셸 명령이 된다.
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] is "-c" or "--config-env")
            {
                var kv = args[i + 1];
                var key = kv.Split('=')[0].ToLowerInvariant();
                if (kv.Contains("=!", StringComparison.Ordinal)
                    || key is "core.fsmonitor" or "core.sshcommand" or "core.hookspath" or "core.pager" or "core.editor" or "sequence.editor" or "core.gitproxy"
                    || key.EndsWith(".pager", StringComparison.Ordinal) || key.StartsWith("filter.", StringComparison.Ordinal)
                    || (key.StartsWith("diff.", StringComparison.Ordinal) && key.EndsWith(".textconv", StringComparison.Ordinal)))
                {
                    return "tools.bashSecurity.shellIndirection";
                }
            }
        }

        if (GitSub(args) is not var (verb, rest))
        {
            return null;
        }

        bool Has(params string[] xs) => rest.Any(a => xs.Contains(a, StringComparer.Ordinal));
        return verb switch
        {
            "push" when rest.Any(a => a is "--force" or "-f" or "--delete" or "-d" or "--mirror" or "--prune"
                                      || a.StartsWith("--force", StringComparison.Ordinal)
                                      || (!a.StartsWith('-') && (a.StartsWith('+') || a.StartsWith(':'))))
                => "tools.bashSecurity.gitForcePush",
            "reset" when Has("--hard", "--merge", "--keep") => "tools.bashSecurity.gitResetHard",
            "clean" when rest.Any(a => a.StartsWith('-') && !a.StartsWith("--", StringComparison.Ordinal) && (a.Contains('f') || a.Contains('d') || a.Contains('x')))
                         || Has("--force") => "tools.bashSecurity.gitClean",
            "checkout" or "restore" when Has(".", "--", "--staged", "--worktree") || rest.Any(a => a is "-f" or "--force")
                => "tools.bashSecurity.gitDiscard",
            "branch" when rest.Any(a => a is "-D" or "--delete" or "-d" or "-M" or "--force") => "tools.bashSecurity.gitDiscard",
            "stash" when Has("drop", "clear") => "tools.bashSecurity.gitDiscard",
            "filter-branch" or "filter-repo" => "tools.bashSecurity.gitDiscard",
            "update-ref" when Has("-d", "--delete") => "tools.bashSecurity.gitDiscard",
            "reflog" when Has("expire", "delete") => "tools.bashSecurity.gitDiscard",
            "gc" when rest.Any(a => a.StartsWith("--prune", StringComparison.Ordinal)) => "tools.bashSecurity.gitDiscard",
            _ => null,
        };
    }

    private static string? ContainerReason(List<string> args)
    {
        var at = ShellCommand.FirstOperand(args, 0, new HashSet<string>(StringComparer.Ordinal) { "-H", "--host", "--context", "-c", "--config", "-l", "--log-level" });
        if (at < 0)
        {
            return null;
        }

        var verb = args[at];
        var rest = args.Skip(at + 1).ToList();
        var next = rest.FirstOrDefault(a => !a.StartsWith('-'));
        if (verb is "run" or "create" || (verb == "container" && next is "run" or "create"))
        {
            return HostAccess(rest) ? "tools.bashSecurity.containerHostAccess" : null;
        }

        return verb switch
        {
            "rm" or "rmi" or "kill" or "stop" or "restart" => "tools.bashSecurity.dockerRemove",
            "system" or "builder" or "image" or "container" or "volume" or "network" when next is "prune" or "rm" or "remove" or "kill" or "stop" => "tools.bashSecurity.dockerRemove",
            "compose" when rest.Any(a => a is "down" or "rm" or "stop" or "kill") => "tools.bashSecurity.dockerRemove",
            _ => null,
        };
    }

    // 컨테이너가 호스트를 건드릴 수 있는 옵션: 특권, 호스트 PID/네임스페이스, 호스트 루트·시스템 경로·도커 소켓 마운트.
    private static bool HostAccess(List<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "--privileged" or "--pid=host" or "--ipc=host" or "--uts=host" or "--userns=host" or "--cgroupns=host"
                || (a is "--pid" or "--ipc" or "--uts" && i + 1 < args.Count && args[i + 1] == "host")
                || (a.StartsWith("--cap-add", StringComparison.Ordinal)
                    && (a.Contains("ALL", StringComparison.OrdinalIgnoreCase) || a.Contains("SYS_ADMIN", StringComparison.OrdinalIgnoreCase)
                        || (i + 1 < args.Count && args[i + 1].ToUpperInvariant() is "ALL" or "SYS_ADMIN"))))
            {
                return true;
            }

            var spec = a is "-v" or "--volume" or "--mount" && i + 1 < args.Count ? args[i + 1]
                : a.StartsWith("--volume=", StringComparison.Ordinal) || a.StartsWith("--mount=", StringComparison.Ordinal) ? a[(a.IndexOf('=') + 1)..]
                : a.StartsWith("-v", StringComparison.Ordinal) && a.Length > 2 && a[2] != '-' ? a[2..]
                : null;
            if (spec is null)
            {
                continue;
            }

            var src = spec.Contains("source=", StringComparison.Ordinal) || spec.Contains("src=", StringComparison.Ordinal)
                ? spec.Split(',').Select(x => x.Split('=')).Where(x => x.Length == 2 && x[0] is "source" or "src").Select(x => x[1]).FirstOrDefault() ?? string.Empty
                : spec.Split(':')[0];
            if (src.StartsWith('/'))
            {
                var n = ShellCommand.NormalizePath(src);
                if (n is "/" or "/dev" or "/proc" or "/sys" or "/home" or "/var" || IsSystemPath(n) || n.Contains("docker.sock", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // ---------- SQL · 코드 ----------

    // DB 클라이언트에 실리는 SQL: 옵션 값(-c/--command/-e/--execute/-q/--query, 붙은 형태 포함), 일반 인자(redis-cli FLUSHALL 등), heredoc/here-string.
    private static IEnumerable<string> SqlParts(ShellCommand.Parsed p)
    {
        foreach (var a in p.Words.Skip(1))
        {
            if (!a.StartsWith('-'))
            {
                yield return a;
            }
            else if (a.Length > 2 && !a.StartsWith("--", StringComparison.Ordinal) && a[1] is 'c' or 'e' or 'q')
            {
                yield return a[2..];
            }
            else if (a.StartsWith("--", StringComparison.Ordinal) && a.Contains('='))
            {
                yield return a[(a.IndexOf('=') + 1)..];
            }
        }

        if (p.Stdin is not null)
        {
            yield return p.Stdin;
        }
    }

    private static string? SqlReason(string sql)
    {
        foreach (var (pattern, reasonKey) in SqlPatterns)
        {
            if (pattern.IsMatch(sql))
            {
                return L10n.Get(reasonKey);
            }
        }

        return null;
    }

    // 프로그램 코드(python -c, 스크립트 파일): 삭제·셸 실행 API, 문자열 리터럴 안의 셸 명령.
    private static string? CodeReason(string code)
    {
        if (DeleteApi.IsMatch(code))
        {
            return L10n.Get("tools.bashSecurity.deleteApi");
        }

        foreach (var p in ShellCommand.ParseCode(code))
        {
            if (p.Words.Count == 0 || p.Words[0] == "(code)")
            {
                continue;
            }

            if (ProgramReason(p.Words) is { } key)
            {
                return L10n.Get(key);
            }

            if (Prog(p.Words[0]) == "rm" && Rm.NeedsConfirmation(string.Join(' ', p.Words)))
            {
                return L10n.Get("tools.bashSecurity.recursiveDelete");
            }
        }

        return null;
    }

    // /* … */ 와 -- 줄 주석을 공백으로 — `DROP/**/TABLE`, `DROP -- x\nTABLE` 우회 방지. 작은따옴표 문자열 안은 건드리지 않는다.
    private static string StripSqlComments(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\'')
            {
                var end = s.IndexOf('\'', i + 1);
                end = end < 0 ? s.Length - 1 : end;
                sb.Append(s, i, end - i + 1);
                i = end;
                continue;
            }

            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                sb.Append(' ');
                i = end < 0 ? s.Length : end + 1;
                continue;
            }

            if (c == '-' && i + 1 < s.Length && s[i + 1] == '-')
            {
                var end = s.IndexOf('\n', i);
                sb.Append(' ');
                i = end < 0 ? s.Length : end - 1;
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    // NonBacktracking: 입력 길이에 선형인 매칭을 보장한다. 이 검사는 모델이 만든 임의 길이 명령에 대해
    // 셸 실행 전 동기로 돌므로(Bash 타임아웃 밖), 역추적 엔진이면 패턴 하나만 잘못돼도 턴이 무한정 멈춘다
    // (2026-10-09: `(?:[^|]*\|[^|]*)+` 가 파이프 많은 curl 명령에서 1000초 넘게 돎). 전후방 탐색·역참조는 쓸 수 없다.
    private static Regex Rx(string p) =>
        new(p, RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    public static bool IsReadOnlyCommand(string command)
    {
        var first = FirstToken(command);
        return first is not null && ReadOnlyCommands.Contains(first);
    }

    private static string? FirstToken(string command)
    {
        var trimmed = command.TrimStart();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var end = trimmed.IndexOfAny(new[] { ' ', '\t' });
        var token = end < 0 ? trimmed : trimmed[..end];
        var slash = token.LastIndexOf('/');
        return slash < 0 ? token : token[(slash + 1)..];
    }
}
