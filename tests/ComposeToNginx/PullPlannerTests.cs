using Shiron.ComposeToNginx.Core.Npm;
using Shiron.ComposeToNginx.Core.Planning;
using Xunit;

namespace Shiron.ComposeToNginx.Tests;

public class PullPlannerTests {
    private readonly PullPlanner _planner = new();

    private static NpmProxyHostInfo Host(
        int id, string domain, string forwardHost, int forwardPort,
        string scheme = "http", int? certId = null, bool? sslForced = null, bool enabled = true,
        string[]? extraDomains = null
    ) {
        var domains = extraDomains is null ? new[] { domain } : new[] { domain }.Concat(extraDomains).ToArray();
        return new NpmProxyHostInfo(id, domains, forwardHost, forwardPort, scheme, certId, sslForced, enabled);
    }

    private static NpmCertificateInfo Cert(int id, string niceName, params string[] domains) =>
        new(id, niceName, domains, "letsencrypt", null);

    // ── Matching ───────────────────────────────────────────────────────────

    [Fact]
    public void Plan_ServicePortMatchesHost_PlannedWithHostLabel() {
        var services = new[] {
            TestServiceFactory.MakeService(name: "api", containerName: "api", ports: ["8080:80"]),
        };
        var hosts = new[] { Host(1, "api.example.com", "api", 8080) };

        var plan = _planner.Plan(services, hosts, []);

        Assert.Single(plan.Planned);
        Assert.Empty(plan.Skipped);
        Assert.True(plan.Planned[0].Labels.ContainsKey("npm.host"));
        Assert.Equal("api.example.com", plan.Planned[0].Labels["npm.host"]);
    }

    [Fact]
    public void Plan_AlreadyManagedService_Skipped() {
        var services = new[] {
            TestServiceFactory.MakeService(name: "api", ports: ["8080:80"],
                labels: new() { ["npm.host"] = "api.example.com" }),
        };
        var hosts = new[] { Host(1, "api.example.com", "api", 8080) };

        var plan = _planner.Plan(services, hosts, []);

        Assert.Empty(plan.Planned);
        Assert.Single(plan.Skipped);
        Assert.Equal(PullSkipReason.AlreadyManaged, plan.Skipped[0].Reason);
    }

    [Fact]
    public void Plan_ServiceWithoutPorts_SkippedAsNoPorts() {
        var services = new[] { TestServiceFactory.MakeService(name: "worker", ports: []) };
        var hosts = new[] { Host(1, "api.example.com", "api", 8080) };

        var plan = _planner.Plan(services, hosts, []);

        Assert.Empty(plan.Planned);
        Assert.Equal(PullSkipReason.NoPorts, plan.Skipped[0].Reason);
    }

    [Fact]
    public void Plan_ServicePortHasNoMatchingHost_SkippedAsNoMatch() {
        var services = new[] { TestServiceFactory.MakeService(name: "web", ports: ["3000:3000"]) };
        var hosts = new[] { Host(1, "api.example.com", "api", 8080) };

        var plan = _planner.Plan(services, hosts, []);

        Assert.Empty(plan.Planned);
        Assert.Equal(PullSkipReason.NoMatch, plan.Skipped[0].Reason);
    }

    [Fact]
    public void Plan_HostWithoutCorrespondingService_IsIgnored() {
        // An NPM host whose forward port no service publishes must not produce a plan.
        var services = new[] { TestServiceFactory.MakeService(name: "api", ports: ["8080:80"]) };
        var hosts = new[] {
            Host(1, "api.example.com", "api", 8080),
            Host(2, "orphan.example.com", "orphan", 9999),
        };

        var plan = _planner.Plan(services, hosts, []);

        Assert.Single(plan.Planned);
        Assert.Equal(1, plan.Planned[0].MatchedHostId);
    }

    [Fact]
    public void Plan_MatchesOnSecondPublishedPortWhenFirstHasNoHost() {
        var services = new[] {
            TestServiceFactory.MakeService(name: "api", ports: ["9090:90", "8080:80"]),
        };
        var hosts = new[] { Host(7, "api.example.com", "api", 8080) };

        var plan = _planner.Plan(services, hosts, []);

        Assert.Single(plan.Planned);
        Assert.Equal(7, plan.Planned[0].MatchedHostId);
    }

    [Fact]
    public void Plan_MultipleHostsOnSamePort_PrefersForwardHostMatch() {
        var services = new[] {
            TestServiceFactory.MakeService(name: "api", containerName: "api", ports: ["8080:80"]),
        };
        var hosts = new[] {
            Host(1, "other.example.com", "127.0.0.1", 8080),
            Host(2, "api.example.com", "api", 8080),
        };

        var plan = _planner.Plan(services, hosts, []);

        Assert.Equal(2, plan.Planned[0].MatchedHostId);
    }

    // ── Label derivation: defaults omitted ──────────────────────────────────

    [Fact]
    public void Plan_ForwardHostEqualsDefault_OmitsForwardHostLabel() {
        var services = new[] {
            TestServiceFactory.MakeService(name: "api", containerName: "api", ports: ["8080:80"]),
        };
        var hosts = new[] { Host(1, "api.example.com", "api", 8080) };

        var labels = _planner.Plan(services, hosts, []).Planned[0].Labels;

        Assert.False(labels.ContainsKey("npm.forward-host"));
    }

    [Fact]
    public void Plan_ForwardPortEqualsDefault_OmitsForwardPortLabel() {
        var services = new[] {
            TestServiceFactory.MakeService(name: "api", ports: ["8080:80"]),
        };
        var hosts = new[] { Host(1, "api.example.com", "127.0.0.1", 8080) };

        var labels = _planner.Plan(services, hosts, []).Planned[0].Labels;

        Assert.False(labels.ContainsKey("npm.forward-port"));
    }

    [Fact]
    public void Plan_ForwardHostDiffersFromDefault_EmitsForwardHostLabel() {
        var services = new[] {
            TestServiceFactory.MakeService(name: "api", containerName: "api", ports: ["8080:80"]),
        };
        var hosts = new[] { Host(1, "api.example.com", "backend.local", 8080) };

        var labels = _planner.Plan(services, hosts, []).Planned[0].Labels;

        Assert.Equal("backend.local", labels["npm.forward-host"]);
    }

    [Fact]
    public void Plan_ForwardPortDiffersFromDefault_EmitsForwardPortLabel() {
        // The host matches the *second* published port (8080); the parser's
        // default forward port is the first (9090), so an override is emitted.
        var services = new[] {
            TestServiceFactory.MakeService(name: "api", ports: ["9090:90", "8080:80"]),
        };
        var hosts = new[] { Host(1, "api.example.com", "127.0.0.1", 8080) };

        var labels = _planner.Plan(services, hosts, []).Planned[0].Labels;

        Assert.Equal("8080", labels["npm.forward-port"]);
    }

    // ── Label derivation: SSL ───────────────────────────────────────────────

    [Fact]
    public void Plan_SslHost_EmitsSslAndCertReferenceFromNiceName() {
        var services = new[] {
            TestServiceFactory.MakeService(name: "api", ports: ["8080:80"]),
        };
        var hosts = new[] { Host(1, "api.example.com", "127.0.0.1", 8080, certId: 5, sslForced: true) };
        var certs = new[] { Cert(5, "Wildcard Example", "*.example.com") };

        var labels = _planner.Plan(services, hosts, certs).Planned[0].Labels;

        Assert.Equal("true", labels["npm.ssl"]);
        Assert.Equal("Wildcard Example", labels["npm.cert"]);
        // force-ssl defaults to true when ssl=true, so it is not emitted.
        Assert.False(labels.ContainsKey("npm.force-ssl"));
    }

    [Fact]
    public void Plan_SslHostWithSslForcedFalse_EmitsForceSslFalse() {
        var services = new[] {
            TestServiceFactory.MakeService(name: "api", ports: ["8080:80"]),
        };
        var hosts = new[] { Host(1, "api.example.com", "127.0.0.1", 8080, certId: 5, sslForced: false) };
        var certs = new[] { Cert(5, "C", "*.example.com") };

        var labels = _planner.Plan(services, hosts, certs).Planned[0].Labels;

        Assert.Equal("false", labels["npm.force-ssl"]);
    }

    [Fact]
    public void Plan_SslHostWithoutMatchingCert_OmitsCertReference() {
        var services = new[] {
            TestServiceFactory.MakeService(name: "api", ports: ["8080:80"]),
        };
        var hosts = new[] { Host(1, "api.example.com", "127.0.0.1", 8080, certId: 99) };

        var labels = _planner.Plan(services, hosts, []).Planned[0].Labels;

        Assert.Equal("true", labels["npm.ssl"]);
        Assert.False(labels.ContainsKey("npm.cert"));
    }

    // ── Label derivation: scheme / enabled ──────────────────────────────────

    [Fact]
    public void Plan_HttpsScheme_EmitsSchemeLabel() {
        var services = new[] {
            TestServiceFactory.MakeService(name: "api", ports: ["8080:80"]),
        };
        var hosts = new[] { Host(1, "api.example.com", "127.0.0.1", 8080, scheme: "https") };

        var labels = _planner.Plan(services, hosts, []).Planned[0].Labels;

        Assert.Equal("https", labels["npm.scheme"]);
    }

    [Fact]
    public void Plan_HttpScheme_OmitsSchemeLabel() {
        var services = new[] {
            TestServiceFactory.MakeService(name: "api", ports: ["8080:80"]),
        };
        var hosts = new[] { Host(1, "api.example.com", "127.0.0.1", 8080, scheme: "http") };

        var labels = _planner.Plan(services, hosts, []).Planned[0].Labels;

        Assert.False(labels.ContainsKey("npm.scheme"));
    }

    [Fact]
    public void Plan_DisabledHost_EmitsEnabledFalse() {
        var services = new[] {
            TestServiceFactory.MakeService(name: "api", ports: ["8080:80"]),
        };
        var hosts = new[] { Host(1, "api.example.com", "127.0.0.1", 8080, enabled: false) };

        var labels = _planner.Plan(services, hosts, []).Planned[0].Labels;

        Assert.Equal("false", labels["npm.enabled"]);
    }

    [Fact]
    public void Plan_EnabledHost_OmitsEnabledLabel() {
        var services = new[] {
            TestServiceFactory.MakeService(name: "api", ports: ["8080:80"]),
        };
        var hosts = new[] { Host(1, "api.example.com", "127.0.0.1", 8080, enabled: true) };

        var labels = _planner.Plan(services, hosts, []).Planned[0].Labels;

        Assert.False(labels.ContainsKey("npm.enabled"));
    }

    // ── Domains ─────────────────────────────────────────────────────────────

    [Fact]
    public void Plan_MultipleDomains_JoinedCommaSeparatedInHostLabel() {
        var services = new[] {
            TestServiceFactory.MakeService(name: "api", ports: ["8080:80"]),
        };
        var hosts = new[] {
            Host(1, "api.example.com", "127.0.0.1", 8080, extraDomains: ["www.example.com", "api2.example.com"]),
        };

        var labels = _planner.Plan(services, hosts, []).Planned[0].Labels;

        Assert.Equal("api.example.com,www.example.com,api2.example.com", labels["npm.host"]);
    }

    // ── Round-trip ──────────────────────────────────────────────────────────

    [Fact]
    public void Plan_GeneratedLabels_AreParseableByLabelConfigParser() {
        // The whole point of pull: the generated labels must be valid input to push.
        var services = new[] {
            TestServiceFactory.MakeService(name: "api", containerName: "api", ports: ["8080:80"]),
        };
        var hosts = new[] {
            Host(1, "api.example.com", "backend", 8080, scheme: "https", certId: 5, sslForced: true),
        };
        var certs = new[] { Cert(5, "Wildcard", "api.example.com") };

        var labels = _planner.Plan(services, hosts, certs).Planned[0].Labels;

        var reparsed = Core.Labels.LabelConfigParser.TryParse(new Lib.DockerUtils.Model.Service {
            Name = "api",
            Image = "x",
            ContainerName = "api",
            Restart = null,
            Ports = new[] { new Lib.DockerUtils.Model.PortForward { ContainerPort = "80", HostPort = "8080" } },
            Volumes = [],
            Environment = [],
            Networks = [],
            Labels = labels.ToDictionary(k => k.Key, k => k.Value),
        });

        Assert.NotNull(reparsed);
        Assert.Equal("api.example.com", reparsed!.Domains[0]);
        Assert.Equal("backend", reparsed.ForwardHost);
        Assert.Equal(8080, reparsed.ForwardPort);
        Assert.Equal("https", reparsed.ForwardScheme);
        Assert.True(reparsed.Ssl);
        Assert.Equal("Wildcard", reparsed.Certificate);
        Assert.True(reparsed.ForceSsl);
    }
}
