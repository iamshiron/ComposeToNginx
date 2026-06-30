using Shiron.ComposeToNginx.Core.Certificates;
using Shiron.ComposeToNginx.Core.Labels;
using Shiron.ComposeToNginx.Core.Npm;
using Shiron.ComposeToNginx.Core.Planning;
using Xunit;

namespace Shiron.ComposeToNginx.Tests;

public class HostPlannerTests {
    private readonly HostPlanner _planner = new(new CertificateResolver());

    private static NpmCertificateInfo Cert(int id, string niceName, params string[] domains) =>
        new(id, niceName, domains, "letsencrypt", null);

    private static HostLabelConfig Cfg(
        string service, string domain, int port = 8080,
        bool ssl = false, string? cert = null, bool enabled = true
    ) =>
        new(service, [domain], "host", port, "http", ssl, cert, ssl, ssl, true, true, false, false, enabled);

    // ── Categorise ───────────────────────────────────────────────────────

    [Fact]
    public void Categorise_Auto_LabelledServiceBecomesManaged() {
        var services = new[] {
            TestServiceFactory.MakeService(name: "api", ports: ["8080:80"],
                labels: new() { ["npm.host"] = "api.example.com" }),
        };

        var cat = _planner.Categorise(services, LabelMode.Auto);

        Assert.Single(cat.Managed);
        Assert.Empty(cat.UnmanagedWithPort);
        Assert.Empty(cat.SkippedNoPort);
        Assert.Empty(cat.Errors);
    }

    [Fact]
    public void Categorise_Auto_UnlabelledWithPortIsUnmanaged() {
        var services = new[] {
            TestServiceFactory.MakeService(name: "web", ports: ["80:80"]),
        };

        var cat = _planner.Categorise(services, LabelMode.Auto);

        Assert.Empty(cat.Managed);
        Assert.Single(cat.UnmanagedWithPort);
        Assert.Empty(cat.SkippedNoPort);
    }

    [Fact]
    public void Categorise_Auto_ServiceWithoutPortIsSkipped() {
        var services = new[] {
            TestServiceFactory.MakeService(name: "worker", ports: []),
        };

        var cat = _planner.Categorise(services, LabelMode.Auto);

        Assert.Empty(cat.Managed);
        Assert.Empty(cat.UnmanagedWithPort);
        Assert.Single(cat.SkippedNoPort);
    }

    [Fact]
    public void Categorise_Ignore_EverythingIsUnmanaged() {
        var services = new[] {
            TestServiceFactory.MakeService(name: "api", ports: ["8080:80"],
                labels: new() { ["npm.host"] = "api.example.com" }),
        };

        var cat = _planner.Categorise(services, LabelMode.Ignore);

        Assert.Empty(cat.Managed);
        Assert.Single(cat.UnmanagedWithPort);
    }

    [Fact]
    public void Categorise_MalformedLabelRecordedAsError() {
        var services = new[] {
            TestServiceFactory.MakeService(name: "api", ports: ["8080:80"],
                labels: new() { ["npm.host"] = "api.example.com", ["npm.ssl"] = "maybe" }),
        };

        var cat = _planner.Categorise(services, LabelMode.Auto);

        Assert.Empty(cat.Managed);
        Assert.Single(cat.Errors);
        Assert.Contains("npm.ssl", cat.Errors[0].Message);
    }

    // ── PlanLabelled ─────────────────────────────────────────────────────

    [Fact]
    public void PlanLabelled_NonSsl_PlannedWithoutCertificate() {
        var configs = new[] { Cfg("api", "api.example.com") };

        var result = _planner.PlanLabelled(configs, [], ExistingHostIndex.Empty);

        Assert.Empty(result.Errors);
        Assert.Single(result.Planned);
        Assert.Null(result.Planned[0].CertificateId);
    }

    [Fact]
    public void PlanLabelled_SslWithResolvableCert_AttachesCertId() {
        var certs = new[] { Cert(7, "API", "api.example.com") };
        var configs = new[] { Cfg("api", "api.example.com", ssl: true) };

        var result = _planner.PlanLabelled(configs, certs, ExistingHostIndex.Empty);

        Assert.Empty(result.Errors);
        Assert.Equal(7, result.Planned[0].CertificateId);
    }

    [Fact]
    public void PlanLabelled_SslAutoDerivesCertFromDomain() {
        var certs = new[] { Cert(1, "Wild", "*.example.com") };
        var configs = new[] { Cfg("api", "api.example.com", ssl: true) };

        var result = _planner.PlanLabelled(configs, certs, ExistingHostIndex.Empty);

        Assert.Empty(result.Errors);
        Assert.Equal(1, result.Planned[0].CertificateId);
    }

    [Fact]
    public void PlanLabelled_SslWithMissingCert_RecordsErrorAndSkips() {
        var configs = new[] { Cfg("api", "api.example.com", ssl: true, cert: "nope") };

        var result = _planner.PlanLabelled(configs, [], ExistingHostIndex.Empty);

        Assert.Empty(result.Planned);
        Assert.Single(result.Errors);
        Assert.Contains("api", result.Errors[0]);
        Assert.Contains("nope", result.Errors[0]);
    }

    [Fact]
    public void PlanLabelled_ExistingHostWithSameDomain_SetsOverwriteId() {
        var existing = ExistingHostIndex.From(new[] {
            new NpmProxyHostInfo(42, ["api.example.com"], "host", 8080, "http", null, false, true),
        });
        var configs = new[] { Cfg("api", "api.example.com") };

        var result = _planner.PlanLabelled(configs, [], existing);

        Assert.Equal(42, result.Planned[0].OverwritesHostId);
    }

    [Fact]
    public void PlanLabelled_PreservesLabelConfigFields() {
        var cfg = new HostLabelConfig(
            "api", ["api.example.com"], "backend", 9000, "https",
            true, null, false, false, false, false, true, true, true
        );
        var certs = new[] { Cert(3, "c", "api.example.com") };

        var result = _planner.PlanLabelled([cfg], certs, ExistingHostIndex.Empty);

        var p = result.Planned[0];
        Assert.Equal("backend", p.ForwardHost);
        Assert.Equal(9000, p.ForwardPort);
        Assert.Equal("https", p.ForwardScheme);
        Assert.Equal(3, p.CertificateId);
        Assert.False(p.ForceSsl);
        Assert.True(p.Caching);
        Assert.True(p.Hsts);
    }
}
