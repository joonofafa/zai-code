using System;
using System.Net;
using MoaiCode.Config;
using Xunit;

namespace MoaiCode.Core.Tests;

public class ProxyConfigTests
{
    // 기본은 우회 없음 — 프록시가 유일 경로인 사내망에서 접속이 끊기지 않도록.
    [Fact]
    public void No_bypass_by_default()
    {
        var bypass = ProxyConfig.BuildBypass(null);
        Assert.Empty(bypass);
    }

    // 우회는 opt-in: proxyBypass 에 넣은 호스트만 직결(포트·서브도메인 포함).
    [Fact]
    public void ProxyBypass_setting_and_ports_are_honored()
    {
        var bypass = ProxyConfig.BuildBypass("vip.bccard.ai, gw.internal:8443, .team.local");
        var proxy = new WebProxy("http://proxy.corp:8080") { BypassList = bypass };

        Assert.True(proxy.IsBypassed(new Uri("https://vip.bccard.ai/api/v1/chat")));  // 지정 호스트 → 직결
        Assert.True(proxy.IsBypassed(new Uri("https://gw.internal:8443/v1/chat")));   // 포트 포함
        Assert.True(proxy.IsBypassed(new Uri("https://sub.team.local/y")));           // 서브도메인
        Assert.False(proxy.IsBypassed(new Uri("https://api.anthropic.com/v1")));      // 그 외 → 프록시 경유
    }

    [Fact]
    public void No_proxy_url_means_no_proxy_applied()
    {
        Assert.Null(ProxyConfig.Apply(proxyUrl: null, proxyUser: null));
        Assert.Null(ProxyConfig.Apply(proxyUrl: "", proxyUser: null));
    }
}
