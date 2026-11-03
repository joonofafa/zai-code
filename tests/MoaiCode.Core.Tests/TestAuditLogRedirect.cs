using System.Runtime.CompilerServices;
using MoaiCode.Config;

namespace MoaiCode.Core.Tests;

// 권한 게이트를 거치는 테스트가 실제 ~/.mate/logs/audit.jsonl 에 기록을 남기지 않게, 테스트 어셈블리 전체의 감사 로그를 임시 파일로 돌린다.
internal static class TestAuditLogRedirect
{
    [ModuleInitializer]
    internal static void Init() =>
        AuditLog.FilePath = Path.Combine(Path.GetTempPath(), $"mate-test-audit-{Environment.ProcessId}.jsonl");
}
