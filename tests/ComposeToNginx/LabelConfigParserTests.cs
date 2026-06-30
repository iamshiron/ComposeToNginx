using Shiron.ComposeToNginx.Cli.Services;
using Shiron.Lib.DockerUtils.Model;
using Xunit;

namespace Shiron.ComposeToNginx.Tests;

public class LabelConfigParserTests {
    private static Service MakeService(
        string name = "api",
        string? containerName = null,
        string[]? ports = null,
        Dictionary<string, string>? labels = null
    ) {
        var portForwards = (ports ?? []).Select(p => {
            var parts = p.Split(':');
            return new PortForward {
                ContainerPort = parts.Length > 1 ? parts[1] : parts[0],
                HostPort = parts[0],
            };
        }).ToArray();

        return new Service {
            Name = name,
            Image = "test:latest",
            ContainerName = containerName,
            Restart = null,
            Ports = portForwards,
            Volumes = [],
            Environment = [],
            Networks = [],
            Labels = labels ?? [],
        };
    }

    // ── Returns null ──────────────────────────────────────────────────────

    [Fact]
    public void TryParse_NoHostLabel_ReturnsNull() {
        var service = MakeService(labels: new() { ["npm.ssl"] = "true" });
        Assert.Null(LabelConfigParser.TryParse(service));
    }

    [Fact]
    public void TryParse_EmptyHostLabel_ReturnsNull() {
        var service = MakeService(labels: new() { ["npm.host"] = "  " });
        Assert.Null(LabelConfigParser.TryParse(service));
    }

    // ── Defaults ──────────────────────────────────────────────────────────

    [Fact]
    public void TryParse_MinimalHost_AppliesDefaults() {
        var service = MakeService(
            containerName: "my-app",
            ports: ["8080:80"],
            labels: new() { ["npm.host"] = "api.example.com" }
        );

        var cfg = LabelConfigParser.TryParse(service);
        Assert.NotNull(cfg);
        Assert.Equal("api", cfg!.Service);
        Assert.Single(cfg.Domains);
        Assert.Equal("api.example.com", cfg.Domains[0]);
        Assert.Equal("my-app", cfg.ForwardHost);
        Assert.Equal(8080, cfg.ForwardPort);
        Assert.False(cfg.Ssl);
        Assert.Null(cfg.Certificate);
        Assert.False(cfg.ForceSsl);
        Assert.False(cfg.Http2);
        Assert.True(cfg.Websocket);
        Assert.True(cfg.BlockExploits);
        Assert.False(cfg.Caching);
        Assert.False(cfg.Hsts);
        Assert.Equal("http", cfg.ForwardScheme);
        Assert.True(cfg.Enabled);
    }

    [Fact]
    public void TryParse_NoContainerName_DefaultsForwardHostToServiceName() {
        var service = MakeService(
            name: "web",
            ports: ["80:80"],
            labels: new() { ["npm.host"] = "web.example.com" }
        );
        Assert.Equal("web", LabelConfigParser.TryParse(service)!.ForwardHost);
    }

    [Fact]
    public void TryParse_SinglePort_UsedAsForwardPort() {
        var service = MakeService(
            ports: ["3000"],
            labels: new() { ["npm.host"] = "app.example.com" }
        );
        Assert.Equal(3000, LabelConfigParser.TryParse(service)!.ForwardPort);
    }

    // ── Domains ───────────────────────────────────────────────────────────

    [Fact]
    public void TryParse_CommaSeparatedDomains_ProducesSans() {
        var service = MakeService(
            ports: ["80:80"],
            labels: new() { ["npm.host"] = "example.com, www.example.com" }
        );
        var cfg = LabelConfigParser.TryParse(service);
        Assert.Equal(2, cfg!.Domains.Count);
        Assert.Contains("example.com", cfg.Domains);
        Assert.Contains("www.example.com", cfg.Domains);
    }

    [Fact]
    public void TryParse_AliasLabels_AddedAsDomains() {
        var service = MakeService(
            ports: ["80:80"],
            labels: new() {
                ["npm.host"] = "api.example.com",
                ["npm.alias.0"] = "www.example.com",
                ["npm.alias.1"] = "api2.example.com"
            }
        );
        var cfg = LabelConfigParser.TryParse(service);
        Assert.Equal(3, cfg!.Domains.Count);
        Assert.Contains("www.example.com", cfg.Domains);
        Assert.Contains("api2.example.com", cfg.Domains);
    }

    [Fact]
    public void TryParse_DuplicateDomains_Deduped() {
        var service = MakeService(
            ports: ["80:80"],
            labels: new() {
                ["npm.host"] = "api.example.com, API.example.com",
                ["npm.alias.0"] = "api.example.com"
            }
        );
        Assert.Single(LabelConfigParser.TryParse(service)!.Domains);
    }

    // ── SSL ───────────────────────────────────────────────────────────────

    [Fact]
    public void TryParse_SslTrue_ForceSslAndHttp2DefaultTrue() {
        var service = MakeService(
            ports: ["80:80"],
            labels: new() {
                ["npm.host"] = "api.example.com",
                ["npm.ssl"] = "true"
            }
        );
        var cfg = LabelConfigParser.TryParse(service);
        Assert.True(cfg!.Ssl);
        Assert.True(cfg.ForceSsl);
        Assert.True(cfg.Http2);
    }

    [Fact]
    public void TryParse_SslTrue_ForceSslOverriddenToFalse() {
        var service = MakeService(
            ports: ["80:80"],
            labels: new() {
                ["npm.host"] = "api.example.com",
                ["npm.ssl"] = "true",
                ["npm.force-ssl"] = "false"
            }
        );
        var cfg = LabelConfigParser.TryParse(service);
        Assert.True(cfg!.Ssl);
        Assert.False(cfg.ForceSsl);
    }

    [Fact]
    public void TryParse_CertLabel_PreservedRaw() {
        var service = MakeService(
            ports: ["80:80"],
            labels: new() {
                ["npm.host"] = "api.example.com",
                ["npm.cert"] = "wildcard.example.com"
            }
        );
        Assert.Equal("wildcard.example.com", LabelConfigParser.TryParse(service)!.Certificate);
    }

    // ── Boolean parsing ───────────────────────────────────────────────────

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("TRUE")]
    [InlineData("Yes")]
    public void TryParse_BoolAcceptsTrueVariants(string value) {
        var service = MakeService(
            ports: ["80:80"],
            labels: new() {
                ["npm.host"] = "api.example.com",
                ["npm.caching"] = value
            }
        );
        Assert.True(LabelConfigParser.TryParse(service)!.Caching);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("off")]
    public void TryParse_BoolAcceptsFalseVariants(string value) {
        var service = MakeService(
            ports: ["80:80"],
            labels: new() {
                ["npm.host"] = "api.example.com",
                ["npm.websocket"] = value
            }
        );
        Assert.False(LabelConfigParser.TryParse(service)!.Websocket);
    }

    [Fact]
    public void TryParse_InvalidBool_Throws() {
        var service = MakeService(
            ports: ["80:80"],
            labels: new() {
                ["npm.host"] = "api.example.com",
                ["npm.ssl"] = "maybe"
            }
        );
        var ex = Assert.Throws<LabelConfigException>(() => LabelConfigParser.TryParse(service));
        Assert.Contains("npm.ssl", ex.Message);
        Assert.Contains("maybe", ex.Message);
    }

    // ── Scheme ────────────────────────────────────────────────────────────

    [Fact]
    public void TryParse_SchemeHttps() {
        var service = MakeService(
            ports: ["80:80"],
            labels: new() {
                ["npm.host"] = "api.example.com",
                ["npm.scheme"] = "https"
            }
        );
        Assert.Equal("https", LabelConfigParser.TryParse(service)!.ForwardScheme);
    }

    [Fact]
    public void TryParse_InvalidScheme_Throws() {
        var service = MakeService(
            ports: ["80:80"],
            labels: new() {
                ["npm.host"] = "api.example.com",
                ["npm.scheme"] = "ftp"
            }
        );
        var ex = Assert.Throws<LabelConfigException>(() => LabelConfigParser.TryParse(service));
        Assert.Contains("npm.scheme", ex.Message);
    }

    // ── Forward host / port overrides ─────────────────────────────────────

    [Fact]
    public void TryParse_ForwardHostOverride() {
        var service = MakeService(
            containerName: "app",
            ports: ["80:80"],
            labels: new() {
                ["npm.host"] = "api.example.com",
                ["npm.forward-host"] = "backend.local"
            }
        );
        Assert.Equal("backend.local", LabelConfigParser.TryParse(service)!.ForwardHost);
    }

    [Fact]
    public void TryParse_ForwardPortOverride() {
        var service = MakeService(
            ports: ["80:80"],
            labels: new() {
                ["npm.host"] = "api.example.com",
                ["npm.forward-port"] = "3000"
            }
        );
        Assert.Equal(3000, LabelConfigParser.TryParse(service)!.ForwardPort);
    }

    [Fact]
    public void TryParse_ForwardPort_NoPortsAndNoOverride_Throws() {
        var service = MakeService(
            ports: [],
            labels: new() { ["npm.host"] = "api.example.com" }
        );
        var ex = Assert.Throws<LabelConfigException>(() => LabelConfigParser.TryParse(service));
        Assert.Contains("npm.forward-port", ex.Message);
    }

    [Fact]
    public void TryParse_ForwardPort_OutOfRange_Throws() {
        var service = MakeService(
            ports: ["80:80"],
            labels: new() {
                ["npm.host"] = "api.example.com",
                ["npm.forward-port"] = "99999"
            }
        );
        Assert.Throws<LabelConfigException>(() => LabelConfigParser.TryParse(service));
    }

    // ── Domain validation ─────────────────────────────────────────────────

    [Fact]
    public void TryParse_InvalidDomain_Throws() {
        var service = MakeService(
            ports: ["80:80"],
            labels: new() { ["npm.host"] = "invalid domain!" }
        );
        var ex = Assert.Throws<LabelConfigException>(() => LabelConfigParser.TryParse(service));
        Assert.Contains("not a valid hostname", ex.Message);
    }

    // ── Full config ───────────────────────────────────────────────────────

    [Fact]
    public void TryParse_AllLabels_MappedCorrectly() {
        var service = MakeService(
            name: "api",
            containerName: "api-svc",
            ports: ["8080:80"],
            labels: new() {
                ["npm.host"] = "api.example.com",
                ["npm.ssl"] = "true",
                ["npm.cert"] = "wildcard",
                ["npm.force-ssl"] = "false",
                ["npm.http2"] = "false",
                ["npm.websocket"] = "false",
                ["npm.block-exploits"] = "false",
                ["npm.caching"] = "true",
                ["npm.hsts"] = "true",
                ["npm.scheme"] = "https",
                ["npm.enabled"] = "false",
                ["npm.forward-host"] = "backend",
                ["npm.forward-port"] = "9000"
            }
        );

        var cfg = LabelConfigParser.TryParse(service);
        Assert.NotNull(cfg);
        Assert.Equal("api", cfg!.Service);
        Assert.Equal("api.example.com", cfg.Domains[0]);
        Assert.Equal("backend", cfg.ForwardHost);
        Assert.Equal(9000, cfg.ForwardPort);
        Assert.Equal("https", cfg.ForwardScheme);
        Assert.True(cfg.Ssl);
        Assert.Equal("wildcard", cfg.Certificate);
        Assert.False(cfg.ForceSsl);
        Assert.False(cfg.Http2);
        Assert.False(cfg.Websocket);
        Assert.False(cfg.BlockExploits);
        Assert.True(cfg.Caching);
        Assert.True(cfg.Hsts);
        Assert.False(cfg.Enabled);
    }
}
