using System;
using System.Net;
using MoaiCode.Config;
using Xunit;

namespace MoaiCode.Core.Tests;

public class ProxyConfigTests
{
    // 사내 프록시가 설정돼도 LLM 게이트웨이(BaseUrl 호스트)는 우회 → 장기 SSE 가 프록시 60초 컷을 안 맞음.
    [Fact]
    public void Gateway_host_bypasses_proxy()
    {
        var bypass = ProxyConfig.BuildBypass("https://vip.bccard.ai/api/v1", null);
        var proxy = new WebProxy("http://proxy.corp:8080") { BypassProxyOnLocal = true, BypassList = bypass };

        Assert.True(proxy.IsBypassed(new Uri("https://vip.bccard.ai/api/v1/chat/completions")));  // 게이트웨이 → 직결
        Assert.False(proxy.IsBypassed(new Uri("https://api.anthropic.com/v1/messages")));         // 그 외 → 프록시 경유
    }

    [Fact]
    public void ProxyBypass_setting_and_ports_are_honored()
    {
        var bypass = ProxyConfig.BuildBypass("https://gw.internal:8443/v1", "extra.corp, .team.local");
        var proxy = new WebProxy("http://proxy.corp:8080") { BypassList = bypass };

        Assert.True(proxy.IsBypassed(new Uri("https://gw.internal:8443/v1/chat")));  // 포트 포함 매칭
        Assert.True(proxy.IsBypassed(new Uri("https://extra.corp/x")));
        Assert.True(proxy.IsBypassed(new Uri("https://sub.team.local/y")));          // 서브도메인
        Assert.False(proxy.IsBypassed(new Uri("https://other.com/z")));
    }

    [Fact]
    public void No_proxy_url_means_no_proxy_applied()
    {
        Assert.Null(ProxyConfig.Apply(proxyUrl: null, proxyUser: null));
        Assert.Null(ProxyConfig.Apply(proxyUrl: "", proxyUser: null));
    }
}
