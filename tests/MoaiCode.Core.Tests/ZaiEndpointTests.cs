using MoaiCode.Core;
using Xunit;

namespace MoaiCode.Core.Tests;

// 대화 모델과 WebSearch 가 같은 접속 정보를 봐야 한다. 규칙이 갈리면 한쪽만 조용히 죽는다
// (런처는 OPENAI_*, 코드는 ZAI_* 를 보던 시절 검색만 비활성됐던 적이 있다).
public sealed class ZaiEndpointTests
{
    [Theory]
    [InlineData("https://api.z.ai/api/coding/paas/v4", true)]
    [InlineData("https://api.z.ai/api/coding/paas/v4/", true)]
    [InlineData("https://API.Z.AI/api/coding/paas/v4", true)]
    // 일반 엔드포인트는 Coding Plan 키로 1113(잔액 부족)이 난다 — 공식이 아니다.
    [InlineData("https://api.z.ai/api/paas/v4", false)]
    [InlineData("http://api.z.ai/api/coding/paas/v4", false)]   // https 아님
    [InlineData("https://vip.bccard.ai/api/v1", false)]         // 설정에 남은 옛 사내 주소
    [InlineData("https://evil.example/api/coding/paas/v4", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void Only_the_official_coding_endpoint_is_accepted(string? url, bool expected)
        => Assert.Equal(expected, ZaiEndpoint.IsOfficial(url));

    [Fact]
    public void BaseUrl_falls_back_to_the_default_for_anything_unofficial()
    {
        var prev = Environment.GetEnvironmentVariable("ZAI_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("ZAI_BASE_URL", "https://vip.bccard.ai/api/v1");
            Assert.Equal(ZaiEndpoint.DefaultBaseUrl, ZaiEndpoint.BaseUrl());

            Environment.SetEnvironmentVariable("ZAI_BASE_URL", null);
            Assert.Equal(ZaiEndpoint.DefaultBaseUrl, ZaiEndpoint.BaseUrl());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZAI_BASE_URL", prev);
        }
    }
}
