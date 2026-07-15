using MoaiCode.Tools.Knowledge;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>
/// OrgDatas 클라이언트측 읽기전용 선차단. 서버가 최종 강제하지만, CLI 도 쓰기·다중문을 보내지 않는다
/// (빠른 실패 + 다중 방어). 이 판정이 뚫리면 쓰기 SQL 이 서버로 전달될 수 있으므로 회귀 테스트로 고정.
/// </summary>
public sealed class OrgDatasReadOnlyTests
{
    [Theory]
    [InlineData("SELECT * FROM sales")]
    [InlineData("select region, sum(revenue) from sales group by region")]
    [InlineData("  SELECT 1")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x")]
    [InlineData("EXPLAIN SELECT * FROM sales")]
    [InlineData("SHOW TABLES")]
    [InlineData("DESCRIBE sales")]
    [InlineData("PRAGMA table_info(sales)")]
    [InlineData("SELECT * FROM sales;")]              // 후행 세미콜론(단일문)은 허용
    [InlineData("-- comment\nSELECT * FROM sales")]   // 선행 주석 후 SELECT
    [InlineData("SELECT name FROM t WHERE note = 'a; DROP TABLE x'")] // 리터럴 안 세미콜론은 무해
    public void Accepts_read_only(string sql) =>
        Assert.True(OrgDatasTool.IsReadOnlySql(sql), sql);

    [Theory]
    [InlineData("INSERT INTO sales VALUES (1)")]
    [InlineData("UPDATE sales SET revenue = 0")]
    [InlineData("DELETE FROM sales")]
    [InlineData("DROP TABLE sales")]
    [InlineData("ALTER TABLE sales ADD col x")]
    [InlineData("TRUNCATE sales")]
    [InlineData("SELECT 1; DROP TABLE sales")]        // 다중문(스택) — 두 번째가 쓰기
    [InlineData("SELECT 1; SELECT 2")]                // 다중문 자체 거부
    [InlineData("  ; DELETE FROM sales")]
    [InlineData("CREATE TABLE x (a int)")]
    [InlineData("")]
    public void Rejects_writes_and_stacked(string sql) =>
        Assert.False(OrgDatasTool.IsReadOnlySql(sql), sql);
}
