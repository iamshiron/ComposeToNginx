using Shiron.ComposeToNginx.Cli.Services;
using Shiron.ComposeToNginx.Cli.Services.Impl;
using Xunit;

namespace Shiron.ComposeToNginx.Tests;

public class CertificateResolverTests {
    private static readonly NpmCertificateInfo[] Certs = [
        new(1, "Wildcard Example", ["*.example.com"], "letsencrypt"),
        new(2, "API Cert", ["api.example.com"], "letsencrypt"),
        new(3, "Multi", ["a.example.org", "b.example.org"], "letsencrypt"),
    ];

    private readonly CertificateResolver _resolver = new();

    // ── FindByDomain ──────────────────────────────────────────────────────

    [Fact]
    public void FindByDomain_ExactMatch() {
        Assert.Equal(2, _resolver.FindByDomain(Certs, "api.example.com"));
    }

    [Fact]
    public void FindByDomain_WildcardMatch() {
        Assert.Equal(1, _resolver.FindByDomain(Certs, "www.example.com"));
    }

    [Fact]
    public void FindByDomain_ExactPreferredOverWildcard() {
        // api.example.com matches both cert #2 (exact) and cert #1 (wildcard)
        Assert.Equal(2, _resolver.FindByDomain(Certs, "api.example.com"));
    }

    [Fact]
    public void FindByDomain_NoMatch_ReturnsNull() {
        Assert.Null(_resolver.FindByDomain(Certs, "other.com"));
    }

    [Fact]
    public void FindByDomain_MultiDomainCert() {
        Assert.Equal(3, _resolver.FindByDomain(Certs, "b.example.org"));
    }

    [Fact]
    public void FindByDomain_CaseInsensitive() {
        Assert.Equal(2, _resolver.FindByDomain(Certs, "API.Example.COM"));
    }

    // ── FindByReference ───────────────────────────────────────────────────

    [Fact]
    public void FindByReference_ByNiceName() {
        Assert.Equal(1, _resolver.FindByReference(Certs, "Wildcard Example"));
    }

    [Fact]
    public void FindByReference_NiceNameCaseInsensitive() {
        Assert.Equal(1, _resolver.FindByReference(Certs, "wildcard example"));
    }

    [Fact]
    public void FindByReference_FallsBackToDomainMatch() {
        Assert.Equal(2, _resolver.FindByReference(Certs, "api.example.com"));
    }

    [Fact]
    public void FindByReference_NoMatch_ReturnsNull() {
        Assert.Null(_resolver.FindByReference(Certs, "nonexistent"));
    }

    [Fact]
    public void FindByReference_EmptyCerts_ReturnsNull() {
        Assert.Null(_resolver.FindByReference([], "anything"));
    }
}
