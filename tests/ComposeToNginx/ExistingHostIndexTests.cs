using Shiron.ComposeToNginx.Core.Npm;
using Shiron.ComposeToNginx.Core.Planning;
using Xunit;

namespace Shiron.ComposeToNginx.Tests;

public class ExistingHostIndexTests {
    private static NpmProxyHostInfo Host(int id, string[] domains, int port, int? certId = null, bool? sslForced = null, string? forwardHost = "app") =>
        new(id, domains, forwardHost, port, "http", certId, sslForced, true);

    // ── From / lookup ────────────────────────────────────────────────────

    [Fact]
    public void FindByDomain_MatchesSingleDomain() {
        var index = ExistingHostIndex.From(new[] { Host(1, ["api.example.com"], 8080) });

        Assert.Equal(1, index.FindByDomain("api.example.com")?.Id);
    }

    [Fact]
    public void FindByDomain_ListMatchesRegardlessOfOrder() {
        var index = ExistingHostIndex.From(new[] { Host(1, ["b.example.com", "a.example.com"], 80) });

        Assert.Equal(1, index.FindByDomain(new[] { "a.example.com", "b.example.com" })?.Id);
    }

    [Fact]
    public void FindByDomain_CaseInsensitive() {
        var index = ExistingHostIndex.From(new[] { Host(1, ["api.example.com"], 8080) });

        Assert.Equal(1, index.FindByDomain("API.EXAMPLE.COM")?.Id);
    }

    [Fact]
    public void FindByPort_MatchesForwardPort() {
        var index = ExistingHostIndex.From(new[] { Host(5, ["x.example.com"], 3000) });

        Assert.Equal(5, index.FindByPort(3000)?.Id);
        Assert.Null(index.FindByPort(9999));
    }

    [Fact]
    public void Empty_ReturnsNullForAllLookups() {
        Assert.Null(ExistingHostIndex.Empty.FindByDomain("any.com"));
        Assert.Null(ExistingHostIndex.Empty.FindByPort(80));
    }

    [Fact]
    public void FindByDomain_NoMatch_ReturnsNull() {
        var index = ExistingHostIndex.From(new[] { Host(1, ["api.example.com"], 8080) });

        Assert.Null(index.FindByDomain("other.com"));
    }

    // ── IsIdentical ──────────────────────────────────────────────────────

    private static ExistingHost EHost(int port, int? certId = null, bool? sslForced = null, string forwardHost = "app") =>
        new(1, "api.example.com", ["api.example.com"], forwardHost, port, certId, sslForced);

    [Fact]
    public void IsIdentical_SameTargetAndSsl_ReturnsTrue() {
        var host = EHost(8080, certId: 7, sslForced: true);

        Assert.True(host.IsIdentical("app", 8080, useSsl: true, certificateId: 7));
    }

    [Fact]
    public void IsIdentical_DifferentPort_ReturnsFalse() {
        var host = EHost(8080);

        Assert.False(host.IsIdentical("app", 9000, useSsl: false, certificateId: null));
    }

    [Fact]
    public void IsIdentical_PlannedSslWithoutCert_DoesNotForceSsl() {
        // useSsl=true but no cert → planned ssl forced is false; existing ssl forced false → identical.
        var host = EHost(8080, sslForced: false);

        Assert.True(host.IsIdentical("app", 8080, useSsl: true, certificateId: null));
    }
}
