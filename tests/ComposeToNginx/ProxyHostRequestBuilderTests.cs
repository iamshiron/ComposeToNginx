using Shiron.ComposeToNginx.Core.Planning;
using Xunit;

namespace Shiron.ComposeToNginx.Tests;

public class ProxyHostRequestBuilderTests {
    private static PlannedHost Host(int? certId = null, bool forceSsl = false, bool http2 = false, string scheme = "http") =>
        new("svc", ["svc.example.com"], "host", 8080, scheme, certId is not null, certId,
            forceSsl, http2, true, true, false, false, true, null);

    [Fact]
    public void Build_NoCertificate_SslForcedAndHttp2AreFalseEvenWhenFlagsTrue() {
        var host = Host(certId: null, forceSsl: true, http2: true);

        var req = ProxyHostRequestBuilder.Build(host);

        Assert.Null(req.CertificateId);
        Assert.False(req.SslForced);
        Assert.False(req.Http2Support);
    }

    [Fact]
    public void Build_WithCertificate_SslForcedAndHttp2FollowFlags() {
        var host = Host(certId: 5, forceSsl: true, http2: true);

        var req = ProxyHostRequestBuilder.Build(host);

        Assert.Equal(5, req.CertificateId);
        Assert.True(req.SslForced);
        Assert.True(req.Http2Support);
    }

    [Fact]
    public void Build_PreservesCoreFields() {
        var host = Host(certId: 5);

        var req = ProxyHostRequestBuilder.Build(host);

        Assert.Equal(["svc.example.com"], req.DomainNames);
        Assert.Equal("host", req.ForwardHost);
        Assert.Equal(8080, req.ForwardPort);
        Assert.True(req.BlockExploits);
        Assert.True(req.AllowWebsocketUpgrade);
        Assert.True(req.Enabled);
        Assert.False(req.CachingEnabled);
        Assert.False(req.HstsEnabled);
    }

    [Fact]
    public void Build_ForwardsSchemeAsGiven() {
        Assert.Equal("https", ProxyHostRequestBuilder.Build(Host(scheme: "https")).ForwardScheme);
        Assert.Equal("http", ProxyHostRequestBuilder.Build(Host(scheme: "http")).ForwardScheme);
    }
}
