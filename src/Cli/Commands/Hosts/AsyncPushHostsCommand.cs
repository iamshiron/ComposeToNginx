using NginxProxy.Sdk;
using NginxProxy.Sdk.Nginx.ProxyHosts;
using NpmCertificate = NginxProxy.Sdk.Nginx.Certificates.Certificates;
using Shiron.ComposeToNginx.Cli.Services;
using Shiron.Lib.DockerUtils;
using Shiron.Lib.DockerUtils.Model;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace Shiron.ComposeToNginx.Cli.Commands.Hosts;

[Description("Read a Docker Compose file and create NGINX Proxy Manager proxy hosts from each service's exposed port.")]
public sealed class AsyncPushHostsCommand(
    INginxProxySdkFactory sdkFactory,
    IAnsiConsole console,
    IComposeReader composeReader
) : AsyncCommand<AsyncPushHostsCommand.Settings> {
    private const int MinPort = 1;
    private const int MaxPort = 65535;
    private const string DefaultForwardHost = "127.0.0.1";

    private static readonly Regex HostnameRegex = new(
        @"^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)
    );

    public sealed class Settings : NpmConnectionSettings {
        [CommandArgument(0, "<file>")]
        [Description("Path to the docker-compose file to read.")]
        public string? ComposeFile { get; init; }

        [CommandOption("--dry-run")]
        [Description("Preview the proxy hosts without creating them.")]
        public bool DryRun { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken) {
        var path = settings.ComposeFile;
        if (string.IsNullOrWhiteSpace(path)) {
            return console.WriteError("Missing input", new InvalidOperationException("A path to a Docker Compose file is required."));
        }
        if (!System.IO.File.Exists(path)) {
            return console.WriteError("File not found", new FileNotFoundException("The specified Docker Compose file was not found.", path));
        }

        var yaml = await System.IO.File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<Service> services;
        try {
            services = composeReader.Read(yaml);
        } catch (ComposeReadException ex) {
            return console.WriteError("Failed to parse compose file", ex);
        }

        if (services.Count == 0) {
            console.MarkupLine("[yellow]No services found in the compose file.[/]");
            return 0;
        }

        console.MarkupLine("[bold]Generate proxy hosts from a Docker Compose file[/]");
        console.MarkupLine("[grey]Set the defaults, then review each service. Press Enter to accept a suggested value.[/]");
        console.WriteLine();

        var forwardHost = PromptForwardHost();
        var baseDomain = PromptBaseDomain();
        var useSsl = console.Confirm("Use SSL for these hosts?", defaultValue: false);

        NginxProxySdk? sdk = null;
        int? certificateId = null;

        if (useSsl) {
            sdk = await TryCreateSdkAsync(settings, cancellationToken, required: true).ConfigureAwait(false);
            if (sdk is null) return 1;

            var certificates = await FetchCertificatesAsync(sdk, cancellationToken).ConfigureAwait(false);
            if (certificates.Count == 0) {
                console.MarkupLine("[yellow]No certificates available in NGINX Proxy Manager; hosts will be planned without one.[/]");
            } else {
                certificateId = PromptCertificate(certificates);
            }
        } else if (!settings.DryRun) {
            sdk = await TryCreateSdkAsync(settings, cancellationToken, required: true).ConfigureAwait(false);
            if (sdk is null) return 1;
        } else {
            sdk = await TryCreateSdkAsync(settings, cancellationToken, required: false).ConfigureAwait(false);
        }

        var existingIndex = await LoadExistingHostIndexAsync(sdk, cancellationToken).ConfigureAwait(false);

        console.WriteLine();
        console.MarkupLine("[bold]Review each service[/]");

        var planned = new List<PlannedHost>();
        foreach (var service in services) {
            var exposed = service.Ports.Length > 0 ? service.Ports[0] : null;
            if (exposed is null) {
                console.MarkupLine($"  [grey]Skip[/] [bold]{Markup.Escape(service.Name)}[/][grey] — no exposed ports.[/]");
                continue;
            }
            if (!int.TryParse(exposed.HostPort, out var forwardPort) || forwardPort is < MinPort or > MaxPort) {
                console.MarkupLine($"  [grey]Skip[/] [bold]{Markup.Escape(service.Name)}[/][grey] — exposed port '[yellow]{Markup.Escape(exposed.HostPort)}[/][grey]' is not a single numeric port.[/]");
                continue;
            }

            var defaultDomain = $"{service.Name}.{baseDomain}";
            var defaultExisting = FindExisting(existingIndex, defaultDomain);

            var title = $"  [bold]{Markup.Escape(service.Name)}[/]  [grey]→ http://{Markup.Escape(forwardHost)}:{forwardPort}[/]";
            if (defaultExisting is not null) {
                title += $"\n  [yellow]'{Markup.Escape(defaultDomain)}' already exists as host #{defaultExisting.Id} — adding will overwrite it.[/]";
            }

            var decision = console.Prompt(
                new SelectionPrompt<HostOption>()
                    .Title(title)
                    .AddChoices(
                        new HostOption(HostDecision.Include, $"Add as {Markup.Escape(defaultDomain)}"),
                        new HostOption(HostDecision.Custom, "Custom domain…"),
                        new HostOption(HostDecision.Skip, "Skip")
                    )
                    .UseConverter(o => o.Display)
            );

            switch (decision.Decision) {
                case HostDecision.Skip:
                    console.MarkupLine($"    [grey]Skipped '{Markup.Escape(service.Name)}'.[/]");
                    continue;
                case HostDecision.Custom:
                    var customDomain = console.Prompt(
                        new TextPrompt<string>("    Domain name:")
                            .DefaultValue(defaultDomain)
                            .Validate(ValidateHostname)
                    );
                    var customExisting = FindExisting(existingIndex, customDomain);
                    if (customExisting is not null) {
                        console.MarkupLine($"    [yellow]'{Markup.Escape(customDomain)}' already exists as host #{customExisting.Id} — adding will overwrite it.[/]");
                    }
                    planned.Add(new PlannedHost(service.Name, customDomain, forwardHost, forwardPort, useSsl, certificateId, customExisting?.Id));
                    break;
                default:
                    planned.Add(new PlannedHost(service.Name, defaultDomain, forwardHost, forwardPort, useSsl, certificateId, defaultExisting?.Id));
                    break;
            }
        }

        if (planned.Count == 0) {
            console.MarkupLine("[yellow]No proxy hosts to plan (no services with usable exposed ports).[/]");
            return 0;
        }

        console.WriteLine();
        RenderOverview(planned);

        console.WriteLine();
        if (settings.DryRun) {
            console.MarkupLine($"[green]Dry run complete.[/] [grey]{planned.Count} host(s) would be created. No changes were made.[/]");
            return 0;
        }

        if (sdk is null) return 1;

        var toDelete = planned.Where(p => p.OverwritesHostId is not null).ToList();
        var deleteSummary = toDelete.Count > 0 ? $", delete {toDelete.Count} conflicting" : "";
        if (!console.Confirm($"Create {planned.Count} proxy host(s){deleteSummary} in NGINX Proxy Manager?", defaultValue: false)) {
            console.MarkupLine("[yellow]Cancelled. No changes were made.[/]");
            return 0;
        }

        return await PushHostsAsync(sdk, planned, toDelete, cancellationToken).ConfigureAwait(false);
    }

    private string PromptForwardHost() =>
        console.Prompt(
            new TextPrompt<string>("Default forward host [grey](where NGINX Proxy Manager sends traffic)[/]:")
                .DefaultValue(DefaultForwardHost)
                .Validate(ValidateHostname)
        );

    private string PromptBaseDomain() =>
        console.Prompt(
            new TextPrompt<string>("Default base domain [grey](e.g. example.com)[/]:")
                .Validate(ValidateHostname)
        );

    private async Task<NginxProxySdk?> TryCreateSdkAsync(Settings settings, CancellationToken cancellationToken, bool required) {
        NpmConnectionOptions options;
        try {
            options = settings.ToConnectionOptions();
        } catch (InvalidOperationException ex) {
            if (required) {
                console.WriteError("Configuration error", ex);
            } else {
                console.MarkupLine("[yellow]NPM connection not configured; overwrite detection is disabled.[/]");
            }
            return null;
        }

        try {
            return await sdkFactory.CreateAsync(options, cancellationToken).ConfigureAwait(false);
        } catch (Exception ex) {
            if (required) {
                console.WriteError("Authentication failed", ex);
            } else {
                console.MarkupLine($"[yellow]Could not authenticate with NGINX Proxy Manager ({Markup.Escape(ex.Message)}); overwrite detection is disabled.[/]");
            }
            return null;
        }
    }

    private async Task<Dictionary<string, ExistingHost>> LoadExistingHostIndexAsync(NginxProxySdk? sdk, CancellationToken cancellationToken) {
        if (sdk is null) return [];

        try {
            var hosts = await sdk.Nginx.ProxyHosts.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return IndexExistingHosts(hosts ?? []);
        } catch (Exception ex) {
            console.MarkupLine($"[yellow]Could not load existing hosts ({Markup.Escape(ex.Message)}); overwrite detection is disabled.[/]");
            return [];
        }
    }

    private static Dictionary<string, ExistingHost> IndexExistingHosts(IReadOnlyList<ProxyHosts> hosts) {
        var index = new Dictionary<string, ExistingHost>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in hosts) {
            var sig = DomainSignature(host.DomainNames);
            if (sig is null) continue;
            if (!index.ContainsKey(sig)) {
                index[sig] = new ExistingHost(host.Id, sig);
            }
        }
        return index;
    }

    private static string? DomainSignature(IReadOnlyList<string>? domains) {
        if (domains is null || domains.Count == 0) return null;
        return string.Join(',', domains.Select(d => d.Trim().ToLowerInvariant()).OrderBy(d => d));
    }

    private static ExistingHost? FindExisting(Dictionary<string, ExistingHost> index, string domain) {
        var sig = DomainSignature([domain]);
        return sig is not null && index.TryGetValue(sig, out var existing) ? existing : null;
    }

    private async Task<List<NpmCertificate>> FetchCertificatesAsync(NginxProxySdk sdk, CancellationToken cancellationToken) {
        try {
            return await sdk.Nginx.Certificates.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
        } catch (Exception ex) {
            console.MarkupLine($"[yellow]Could not load certificates ({Markup.Escape(ex.Message)}); continuing without a selection.[/]");
            return [];
        }
    }

    private int? PromptCertificate(List<NpmCertificate> certificates) {
        var choices = new List<CertificateChoice> { new(null, "None") };
        choices.AddRange(certificates.Select(ToCertificateChoice));

        var selected = console.Prompt(
            new SelectionPrompt<CertificateChoice>()
                .Title("Certificate")
                .AddChoices(choices)
                .UseConverter(c => c.Display)
        );
        return selected.Id;
    }

    private static CertificateChoice ToCertificateChoice(NpmCertificate cert) {
        var label = cert.DomainNames is { Count: > 0 } names
            ? string.Join(", ", names)
            : cert.NiceName ?? $"Certificate #{cert.Id}";
        return new CertificateChoice(cert.Id, $"#{cert.Id} — {label}");
    }

    private static ValidationResult ValidateHostname(string input) {
        var value = input.Trim();
        if (value.Length == 0) return ValidationResult.Error("[red]A value is required.[/]");
        return HostnameRegex.IsMatch(value)
            ? ValidationResult.Success()
            : ValidationResult.Error("[red]Enter a valid hostname (letters, digits, dots, hyphens).[/]");
    }

    private void RenderOverview(List<PlannedHost> planned) {
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Planned proxy hosts[/]");
        table.AddColumn("Service");
        table.AddColumn("Domain");
        table.AddColumn("Forwards to");
        table.AddColumn(new TableColumn("SSL").Centered());
        table.AddColumn(new TableColumn("Status").Centered());

        foreach (var h in planned) {
            var forward = $"http://{h.ForwardHost}:{h.ForwardPort}";
            var ssl = h.Ssl
                ? (h.CertificateId is null ? "[yellow]On (no cert)[/]" : $"[green]On[/] [grey]#{h.CertificateId}[/]")
                : "[grey]Off[/]";
            var status = h.OverwritesHostId is { } id ? $"[yellow]Overwrites #{id}[/]" : "[green]New[/]";
            table.AddRow(Markup.Escape(h.Service), Markup.Escape(h.Domain), Markup.Escape(forward), ssl, status);
        }

        console.Write(table);
    }

    private async Task<int> PushHostsAsync(NginxProxySdk sdk, List<PlannedHost> planned, List<PlannedHost> toDelete, CancellationToken cancellationToken) {
        var deleted = 0;
        var deleteFailed = 0;

        if (toDelete.Count > 0) {
            console.MarkupLine($"[yellow]Deleting {toDelete.Count} conflicting host(s)…[/]");
            foreach (var h in toDelete) {
                if (h.OverwritesHostId is not int id) continue;
                try {
                    await sdk.Nginx.ProxyHosts[id].DeleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    console.MarkupLine($"  [red]Deleted[/] host #{id} [grey]({Markup.Escape(h.Domain)})[/]");
                    deleted++;
                } catch (Exception ex) {
                    console.MarkupLine($"  [red]Failed to delete host #{id}:[/] {Markup.Escape(ex.Message)}");
                    deleteFailed++;
                }
            }
        }

        var created = 0;
        var createFailed = 0;

        console.MarkupLine($"[green]Creating {planned.Count} proxy host(s)…[/]");
        foreach (var h in planned) {
            try {
                var body = BuildRequestBody(h);
                var response = await sdk.Nginx.ProxyHosts.PostAsProxyHostsPostResponseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
                console.MarkupLine($"  [green]Created[/] [bold]{Markup.Escape(h.Service)}[/] → {Markup.Escape(h.Domain)} [grey](#{response?.Id})[/]");
                created++;
            } catch (Exception ex) {
                console.MarkupLine($"  [red]Failed to create {Markup.Escape(h.Domain)}:[/] {Markup.Escape(ex.Message)}");
                createFailed++;
            }
        }

        console.WriteLine();
        var totalFailed = deleteFailed + createFailed;
        if (totalFailed == 0) {
            console.MarkupLine($"[green]Done.[/] {created} created, {deleted} deleted.");
        } else {
            console.MarkupLine($"[yellow]Completed with {totalFailed} failure(s).[/] {created} created, {deleted} deleted.");
        }
        return totalFailed > 0 ? 1 : 0;
    }

    private static ProxyHostsPostRequestBody BuildRequestBody(PlannedHost h) {
        var hasCert = h.Ssl && h.CertificateId is not null;
        return new ProxyHostsPostRequestBody {
            DomainNames = [h.Domain],
            ForwardScheme = ProxyHostsPostRequestBody_forward_scheme.Http,
            ForwardHost = h.ForwardHost,
            ForwardPort = h.ForwardPort,
            CertificateId = h.CertificateId is null ? null : new() { Integer = h.CertificateId },
            SslForced = hasCert,
            Http2Support = hasCert,
            HstsEnabled = false,
            BlockExploits = true,
            CachingEnabled = false,
            AllowWebsocketUpgrade = true,
            Enabled = true,
        };
    }

    private sealed record PlannedHost(string Service, string Domain, string ForwardHost, int ForwardPort, bool Ssl, int? CertificateId, int? OverwritesHostId);

    private sealed record ExistingHost(int? Id, string Signature);

    private sealed record CertificateChoice(int? Id, string Display);

    private enum HostDecision { Include, Custom, Skip }

    private sealed record HostOption(HostDecision Decision, string Display);
}
