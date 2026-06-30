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

/// <summary>
/// Reads a Docker Compose file and creates proxy hosts in NGINX Proxy Manager
/// from each service's exposed port and/or <c>npm.*</c> labels.
/// </summary>
[Description("Read a Docker Compose file and create NGINX Proxy Manager proxy hosts from each service's exposed port.")]
public sealed class AsyncPushHostsCommand(
    INginxProxySdkFactory sdkFactory,
    ICertificateResolver certificateResolver,
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

    public sealed class Settings : NpmMutationSettings {
        [CommandArgument(0, "<file>")]
        [Description("Path to the docker-compose file to read.")]
        public string? ComposeFile { get; init; }

        [CommandOption("--label-mode")]
        [Description("How to treat npm.* labels: auto (use when present, prompt otherwise — default), require (error if a ported service has no label), ignore (always prompt).")]
        [DefaultValue(LabelMode.Auto)]
        public LabelMode LabelMode { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken) {
        // ── 1. Validate + read ────────────────────────────────────────────
        var path = settings.ComposeFile;
        if (string.IsNullOrWhiteSpace(path)) {
            return console.WriteError("Missing input", new InvalidOperationException("A path to a Docker Compose file is required."));
        }
        if (!File.Exists(path)) {
            return console.WriteError("File not found", new FileNotFoundException("The specified Docker Compose file was not found.", path));
        }

        var yaml = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

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

        // ── 2. Categorise services ────────────────────────────────────────
        var labelMode = settings.LabelMode;
        var nonInteractive = settings.IsNonInteractive;

        var labelled = new List<HostLabelConfig>();
        var unmanaged = new List<Service>();
        var labelErrors = new List<LabelConfigException>();

        foreach (var service in services) {
            var config = TryCategorise(service, labelMode);
            switch (config) {
                case CategoriseResult.Managed managed:
                    labelled.Add(managed.Config);
                    break;
                case CategoriseResult.Error error:
                    labelErrors.Add(error.Exception);
                    break;
                case CategoriseResult.Unmanaged:
                    if (HasUsablePort(service))
                        unmanaged.Add(service);
                    else
                        console.MarkupLine($"  [grey]Skip[/] [bold]{Markup.Escape(service.Name)}[/][grey] — no exposed ports.[/]");
                    break;
            }
        }

        if (labelErrors.Count > 0) {
            console.MarkupLine("[red]Aborting: invalid npm.* labels detected.[/]");
            foreach (var ex in labelErrors)
                console.MarkupLine($"  [red]•[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        if (labelMode == LabelMode.Require && unmanaged.Count > 0) {
            console.MarkupLine("[red]Aborting: --label-mode=require but the following services have ports but no npm.host label:[/]");
            foreach (var s in unmanaged)
                console.MarkupLine($"  [red]•[/] [bold]{Markup.Escape(s.Name)}[/]");
            return 1;
        }

        var anyLabelled = labelled.Count > 0;
        var needInteractiveFallback = unmanaged.Count > 0 && !nonInteractive;

        console.MarkupLine("[bold]Generate proxy hosts from a Docker Compose file[/]");
        if (anyLabelled && !needInteractiveFallback)
            console.MarkupLine("[grey]Label-driven mode — no prompts.[/]");
        console.WriteLine();

        // ── 3. Connect + fetch reference data ─────────────────────────────
        var anySsl = labelled.Any(l => l.Ssl) || needInteractiveFallback;
        var sdkRequired = !settings.DryRun || anySsl;

        var sdk = await TryCreateSdkAsync(settings, cancellationToken, required: sdkRequired).ConfigureAwait(false);
        if (sdk is null && sdkRequired) return 1;

        IReadOnlyList<NpmCertificateInfo> certs = [];
        if (anySsl && sdk is not null) {
            try {
                certs = await certificateResolver.FetchAsync(sdk, cancellationToken).ConfigureAwait(false);
            } catch (Exception ex) {
                if (labelled.Any(l => l.Ssl)) {
                    return console.WriteError("Failed to load certificates", ex);
                }
                console.MarkupLine($"[yellow]Could not load certificates ({Markup.Escape(ex.Message)}); continuing without selection.[/]");
            }
        }

        var existingIndex = await LoadExistingHostIndexAsync(sdk, cancellationToken).ConfigureAwait(false);

        // ── 4. Accumulate (transaction) ───────────────────────────────────
        var planned = new List<PlannedHost>();
        var accumulationErrors = new List<string>();

        // 4a. Label-driven hosts
        foreach (var cfg in labelled) {
            if (!cfg.Enabled) {
                console.MarkupLine($"  [grey]Skip[/] [bold]{Markup.Escape(cfg.Service)}[/][grey] — npm.enabled=false.[/]");
                continue;
            }

            int? certId = null;
            if (cfg.Ssl) {
                certId = ResolveCertificate(cfg, certs);
                if (certId is null) {
                    var domainList = string.Join(", ", cfg.Domains);
                    if (cfg.Certificate is not null)
                        accumulationErrors.Add($"Service '{cfg.Service}': certificate '{cfg.Certificate}' not found in NPM (checked nice-name and domain coverage for [{domainList}]).");
                    else
                        accumulationErrors.Add($"Service '{cfg.Service}': no certificate found covering [{domainList}]. Add one to NPM or set npm.cert.");
                    continue;
                }
            }

            var overwriteId = FindOverwriteTarget(existingIndex, cfg.Domains);
            planned.Add(ToPlannedHost(cfg, certId, overwriteId));
        }

        // 4b. Interactive fallback for unlabelled services
        InteractiveDefaults? defaults = null;
        if (needInteractiveFallback) {
            defaults = PromptDefaults(certs);
        }

        foreach (var service in unmanaged) {
            var exposed = service.Ports[0];
            if (!int.TryParse(exposed.HostPort, out var forwardPort) || forwardPort is < MinPort or > MaxPort) {
                console.MarkupLine($"  [grey]Skip[/] [bold]{Markup.Escape(service.Name)}[/][grey] — exposed port '[yellow]{Markup.Escape(exposed.HostPort)}[/][grey]' is not a single numeric port.[/]");
                continue;
            }

            if (nonInteractive) {
                console.MarkupLine($"  [grey]Skip[/] [bold]{Markup.Escape(service.Name)}[/][grey] — no npm.host label (non-interactive mode).[/]");
                continue;
            }

            var plannedHost = PromptForService(service, forwardPort, defaults!, existingIndex);
            if (plannedHost is not null)
                planned.Add(plannedHost);
        }

        // ── 5. Abort on accumulation errors ───────────────────────────────
        if (accumulationErrors.Count > 0) {
            console.MarkupLine("[red]Aborting: configuration errors detected during planning.[/]");
            foreach (var e in accumulationErrors)
                console.MarkupLine($"  [red]•[/] {Markup.Escape(e)}");
            return 1;
        }

        if (planned.Count == 0) {
            console.MarkupLine("[yellow]No proxy hosts to create.[/]");
            return 0;
        }

        // ── 6. Present ────────────────────────────────────────────────────
        console.WriteLine();
        RenderOverview(planned);

        // ── 7. Dry-run exit ───────────────────────────────────────────────
        console.WriteLine();
        if (settings.DryRun) {
            console.MarkupLine($"[green]Dry run complete.[/] [grey]{planned.Count} host(s) would be created. No changes were made.[/]");
            return 0;
        }

        if (sdk is null) return 1;

        // ── 8. Confirm ────────────────────────────────────────────────────
        var toDelete = planned.Where(p => p.OverwritesHostId is not null).ToList();
        var deleteSummary = toDelete.Count > 0 ? $", delete {toDelete.Count} conflicting" : "";

        if (!nonInteractive) {
            if (!console.Confirm($"Create {planned.Count} proxy host(s){deleteSummary} in NGINX Proxy Manager?", defaultValue: false)) {
                console.MarkupLine("[yellow]Cancelled. No changes were made.[/]");
                return 0;
            }
        }

        // ── 9. Apply ──────────────────────────────────────────────────────
        return await PushHostsAsync(sdk, planned, toDelete, cancellationToken).ConfigureAwait(false);
    }

    // ── Categorisation ────────────────────────────────────────────────────

    private abstract class CategoriseResult {
        public sealed class Managed(HostLabelConfig config) : CategoriseResult {
            public HostLabelConfig Config { get; } = config;
        }
        public sealed class Unmanaged : CategoriseResult;
        public sealed class Error(LabelConfigException exception) : CategoriseResult {
            public LabelConfigException Exception { get; } = exception;
        }
    }

    private static CategoriseResult TryCategorise(Service service, LabelMode labelMode) {
        if (labelMode == LabelMode.Ignore)
            return new CategoriseResult.Unmanaged();

        try {
            var config = LabelConfigParser.TryParse(service);
            return config is not null
                ? new CategoriseResult.Managed(config)
                : new CategoriseResult.Unmanaged();
        } catch (LabelConfigException ex) {
            return new CategoriseResult.Error(ex);
        }
    }

    private static bool HasUsablePort(Service service) =>
        service.Ports.Length > 0;

    // ── Certificate resolution ────────────────────────────────────────────

    private int? ResolveCertificate(HostLabelConfig cfg, IReadOnlyList<NpmCertificateInfo> certs) {
        if (cfg.Certificate is not null)
            return certificateResolver.FindByReference(certs, cfg.Certificate);

        var primary = cfg.Domains[0];
        return certificateResolver.FindByDomain(certs, primary);
    }

    // ── Interactive prompts ───────────────────────────────────────────────

    private sealed record InteractiveDefaults(
        string ForwardHost,
        string BaseDomain,
        bool UseSsl,
        int? CertificateId
    );

    private InteractiveDefaults PromptDefaults(IReadOnlyList<NpmCertificateInfo> certs) {
        console.MarkupLine("[grey]Some services lack npm.* labels — falling back to interactive mode for those.[/]");
        console.WriteLine();

        var forwardHost = console.Prompt(
            new TextPrompt<string>("Default forward host [grey](where NPM sends traffic)[/]:")
                .DefaultValue(DefaultForwardHost)
                .Validate(ValidateHostname)
        );

        var baseDomain = console.Prompt(
            new TextPrompt<string>("Default base domain [grey](e.g. example.com)[/]:")
                .Validate(ValidateHostname)
        );

        var useSsl = console.Confirm("Use SSL for these hosts?", defaultValue: false);

        int? certId = null;
        if (useSsl && certs.Count > 0) {
            certId = PromptCertificate(certs);
        } else if (useSsl) {
            console.MarkupLine("[yellow]No certificates available in NPM; hosts will be planned without one.[/]");
        }

        return new InteractiveDefaults(forwardHost, baseDomain, useSsl, certId);
    }

    private PlannedHost? PromptForService(Service service, int forwardPort, InteractiveDefaults defaults, ExistingHostIndex existingIndex) {
        var defaultDomain = $"{service.Name}.{defaults.BaseDomain}";
        var domainMatch = FindByDomain(existingIndex, defaultDomain);
        var portMatch = FindByPort(existingIndex, forwardPort);
        var identical = domainMatch is not null
            && IsIdentical(domainMatch, defaults.ForwardHost, forwardPort, defaults.UseSsl, defaults.CertificateId);

        var title = $"  [bold]{Markup.Escape(service.Name)}[/]  [grey]→ http://{Markup.Escape(defaults.ForwardHost)}:{forwardPort}[/]";
        var options = new List<HostOption>();
        ExistingHost? overwriteTarget = null;

        if (identical && domainMatch is not null) {
            title += $"\n  [green]Configuration is identical to host #{domainMatch.Id} '{Markup.Escape(defaultDomain)}' — already up to date.[/]";
            options.Add(new HostOption(HostDecision.Skip, "Continue — no changes needed"));
            options.Add(new HostOption(HostDecision.Custom, "Edit…"));
        } else if (domainMatch is not null) {
            title += $"\n  [yellow]'{Markup.Escape(defaultDomain)}' already exists as host #{domainMatch.Id} — adding will overwrite it.[/]";
            overwriteTarget = domainMatch;
            options.Add(new HostOption(HostDecision.Include, $"Add as {Markup.Escape(defaultDomain)}"));
            options.Add(new HostOption(HostDecision.Custom, "Custom domain…"));
            options.Add(new HostOption(HostDecision.Skip, "Skip"));
        } else if (portMatch is not null) {
            var inUseDomain = Markup.Escape(DisplayDomain(portMatch));
            title += $"\n  [yellow]Port {forwardPort} is in use by host #{portMatch.Id} '{inUseDomain}'. Overwrite the hostname to '{Markup.Escape(defaultDomain)}'?[/]";
            overwriteTarget = portMatch;
            options.Add(new HostOption(HostDecision.Include, $"Overwrite hostname to {Markup.Escape(defaultDomain)}"));
            options.Add(new HostOption(HostDecision.Custom, "Custom domain…"));
            options.Add(new HostOption(HostDecision.Skip, "Skip"));
        } else {
            options.Add(new HostOption(HostDecision.Include, $"Add as {Markup.Escape(defaultDomain)}"));
            options.Add(new HostOption(HostDecision.Custom, "Custom domain…"));
            options.Add(new HostOption(HostDecision.Skip, "Skip"));
        }

        var decision = console.Prompt(
            new SelectionPrompt<HostOption>()
                .Title(title)
                .AddChoices(options)
                .UseConverter(o => o.Display)
        );

        switch (decision.Decision) {
            case HostDecision.Skip:
                if (identical) {
                    console.MarkupLine($"    [green]Kept '{Markup.Escape(service.Name)}' — already up to date.[/]");
                } else {
                    console.MarkupLine($"    [grey]Skipped '{Markup.Escape(service.Name)}'.[/]");
                }
                return null;
            case HostDecision.Custom:
                var customDomain = console.Prompt(
                    new TextPrompt<string>("    Domain name:")
                        .DefaultValue(defaultDomain)
                        .Validate(ValidateHostname)
                );
                var customExisting = FindByDomain(existingIndex, customDomain);
                if (customExisting is not null) {
                    console.MarkupLine($"    [yellow]'{Markup.Escape(customDomain)}' already exists as host #{customExisting.Id} — adding will overwrite it.[/]");
                }
                return ToPlannedHostInteractive(service.Name, [customDomain], defaults.ForwardHost, forwardPort, defaults.UseSsl, defaults.CertificateId, customExisting?.Id);
            default:
                return ToPlannedHostInteractive(service.Name, [defaultDomain], defaults.ForwardHost, forwardPort, defaults.UseSsl, defaults.CertificateId, overwriteTarget?.Id);
        }
    }

    private int? PromptCertificate(IReadOnlyList<NpmCertificateInfo> certs) {
        var choices = new List<CertificateChoice> { new(null, "None") };
        choices.AddRange(certs.Select(ToCertificateChoice));

        var selected = console.Prompt(
            new SelectionPrompt<CertificateChoice>()
                .Title("Certificate")
                .AddChoices(choices)
                .UseConverter(c => c.Display)
        );
        return selected.Id;
    }

    private static CertificateChoice ToCertificateChoice(NpmCertificateInfo cert) {
        var label = cert.DomainNames is { Count: > 0 } names
            ? string.Join(", ", names)
            : cert.NiceName ?? $"Certificate #{cert.Id}";
        return new CertificateChoice(cert.Id, $"#{cert.Id} — {label}");
    }

    // ── PlannedHost construction ──────────────────────────────────────────

    private static PlannedHost ToPlannedHost(HostLabelConfig cfg, int? certId, int? overwriteId) =>
        new(
            cfg.Service,
            cfg.Domains,
            cfg.ForwardHost,
            cfg.ForwardPort,
            cfg.ForwardScheme,
            cfg.Ssl,
            certId,
            cfg.ForceSsl,
            cfg.Http2,
            cfg.Websocket,
            cfg.BlockExploits,
            cfg.Caching,
            cfg.Hsts,
            cfg.Enabled,
            overwriteId
        );

    private static PlannedHost ToPlannedHostInteractive(
        string service, IReadOnlyList<string> domains, string forwardHost, int forwardPort,
        bool ssl, int? certId, int? overwriteId
    ) {
        var hasCert = ssl && certId is not null;
        return new PlannedHost(
            service,
            domains,
            forwardHost,
            forwardPort,
            "http",
            ssl,
            certId,
            hasCert,
            hasCert,
            true,
            true,
            false,
            false,
            true,
            overwriteId
        );
    }

    // ── Existing host index ───────────────────────────────────────────────

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
                console.MarkupLine($"[yellow]Could not authenticate with NPM ({Markup.Escape(ex.Message)}); overwrite detection is disabled.[/]");
            }
            return null;
        }
    }

    private async Task<ExistingHostIndex> LoadExistingHostIndexAsync(NginxProxySdk? sdk, CancellationToken cancellationToken) {
        if (sdk is null) return ExistingHostIndex.Empty;

        try {
            var hosts = await sdk.Nginx.ProxyHosts.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return IndexExistingHosts(hosts ?? []);
        } catch (Exception ex) {
            console.MarkupLine($"[yellow]Could not load existing hosts ({Markup.Escape(ex.Message)}); overwrite detection is disabled.[/]");
            return ExistingHostIndex.Empty;
        }
    }

    private static ExistingHostIndex IndexExistingHosts(IReadOnlyList<ProxyHosts> hosts) {
        var byDomain = new Dictionary<string, ExistingHost>(StringComparer.OrdinalIgnoreCase);
        var byPort = new Dictionary<int, ExistingHost>();

        foreach (var host in hosts) {
            var sig = DomainSignature(host.DomainNames);
            var existing = new ExistingHost(
                host.Id,
                sig ?? "",
                host.DomainNames ?? [],
                host.ForwardHost,
                host.ForwardPort,
                ExtractCertificateId(host.CertificateId),
                host.SslForced
            );

            if (sig is not null && !byDomain.ContainsKey(sig)) {
                byDomain[sig] = existing;
            }
            if (host.ForwardPort is int port && !byPort.ContainsKey(port)) {
                byPort[port] = existing;
            }
        }

        return new ExistingHostIndex(byDomain, byPort);
    }

    private static int? ExtractCertificateId(ProxyHosts.ProxyHosts_certificate_id? value) => value?.Integer;

    private static string? DomainSignature(IReadOnlyList<string>? domains) {
        if (domains is null || domains.Count == 0) return null;
        return string.Join(',', domains.Select(d => d.Trim().ToLowerInvariant()).OrderBy(d => d));
    }

    private static int? FindOverwriteTarget(ExistingHostIndex index, IReadOnlyList<string> domains) {
        var sig = DomainSignature(domains);
        return sig is not null && index.ByDomain.TryGetValue(sig, out var existing) ? existing.Id : null;
    }

    private static ExistingHost? FindByDomain(ExistingHostIndex index, string domain) {
        var sig = DomainSignature([domain]);
        return sig is not null && index.ByDomain.TryGetValue(sig, out var existing) ? existing : null;
    }

    private static ExistingHost? FindByPort(ExistingHostIndex index, int port) =>
        index.ByPort.TryGetValue(port, out var existing) ? existing : null;

    private static bool IsIdentical(ExistingHost existing, string forwardHost, int forwardPort, bool useSsl, int? certificateId) {
        if (!string.Equals(existing.ForwardHost, forwardHost, StringComparison.OrdinalIgnoreCase)) return false;
        if (existing.ForwardPort != forwardPort) return false;

        var plannedSslForced = useSsl && certificateId is not null;
        var existingSslForced = existing.SslForced ?? false;
        return plannedSslForced == existingSslForced && existing.CertificateId == certificateId;
    }

    private static string DisplayDomain(ExistingHost host) =>
        host.DomainNames is { Count: > 0 } names ? string.Join(", ", names) : host.Signature;

    // ── Rendering ─────────────────────────────────────────────────────────

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
        table.AddColumn("Domains");
        table.AddColumn("Forwards to");
        table.AddColumn(new TableColumn("SSL").Centered());
        table.AddColumn(new TableColumn("Status").Centered());

        foreach (var h in planned) {
            var domains = string.Join(", ", h.Domains);
            var forward = $"{h.ForwardScheme}://{h.ForwardHost}:{h.ForwardPort}";
            var ssl = h.Ssl
                ? (h.CertificateId is null ? "[yellow]On (no cert)[/]" : $"[green]On[/] [grey]#{h.CertificateId}[/]")
                : "[grey]Off[/]";
            var status = h.OverwritesHostId is { } id ? $"[yellow]Overwrites #{id}[/]" : "[green]New[/]";
            table.AddRow(Markup.Escape(h.Service), Markup.Escape(domains), Markup.Escape(forward), ssl, status);
        }

        console.Write(table);
    }

    // ── Apply ─────────────────────────────────────────────────────────────

    private async Task<int> PushHostsAsync(NginxProxySdk sdk, List<PlannedHost> planned, List<PlannedHost> toDelete, CancellationToken cancellationToken) {
        var deleted = 0;
        var deleteFailed = 0;

        if (toDelete.Count > 0) {
            console.MarkupLine($"[yellow]Deleting {toDelete.Count} conflicting host(s)…[/]");
            foreach (var h in toDelete) {
                if (h.OverwritesHostId is not int id) continue;
                try {
                    await sdk.Nginx.ProxyHosts[id].DeleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    console.MarkupLine($"  [red]Deleted[/] host #{id} [grey]({Markup.Escape(string.Join(", ", h.Domains))})[/]");
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
                console.MarkupLine($"  [green]Created[/] [bold]{Markup.Escape(h.Service)}[/] → {Markup.Escape(string.Join(", ", h.Domains))} [grey](#{response?.Id})[/]");
                created++;
            } catch (Exception ex) {
                console.MarkupLine($"  [red]Failed to create {Markup.Escape(string.Join(", ", h.Domains))}:[/] {Markup.Escape(ex.Message)}");
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
        var hasCert = h.CertificateId is not null;
        var scheme = h.ForwardScheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            ? ProxyHostsPostRequestBody_forward_scheme.Https
            : ProxyHostsPostRequestBody_forward_scheme.Http;

        return new ProxyHostsPostRequestBody {
            DomainNames = h.Domains.ToList(),
            ForwardScheme = scheme,
            ForwardHost = h.ForwardHost,
            ForwardPort = h.ForwardPort,
            CertificateId = h.CertificateId is null ? null : new() { Integer = h.CertificateId },
            SslForced = hasCert && h.ForceSsl,
            Http2Support = hasCert && h.Http2,
            HstsEnabled = h.Hsts,
            BlockExploits = h.BlockExploits,
            CachingEnabled = h.Caching,
            AllowWebsocketUpgrade = h.Websocket,
            Enabled = h.Enabled,
        };
    }

    // ── Types ─────────────────────────────────────────────────────────────

    internal sealed record PlannedHost(
        string Service,
        IReadOnlyList<string> Domains,
        string ForwardHost,
        int ForwardPort,
        string ForwardScheme,
        bool Ssl,
        int? CertificateId,
        bool ForceSsl,
        bool Http2,
        bool Websocket,
        bool BlockExploits,
        bool Caching,
        bool Hsts,
        bool Enabled,
        int? OverwritesHostId
    );

    private sealed record ExistingHost(
        int? Id,
        string Signature,
        IReadOnlyList<string> DomainNames,
        string? ForwardHost,
        int? ForwardPort,
        int? CertificateId,
        bool? SslForced
    );

    private sealed record ExistingHostIndex(
        IReadOnlyDictionary<string, ExistingHost> ByDomain,
        IReadOnlyDictionary<int, ExistingHost> ByPort
    ) {
        public static ExistingHostIndex Empty { get; } = new(
            new Dictionary<string, ExistingHost>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<int, ExistingHost>()
        );
    }

    private sealed record CertificateChoice(int? Id, string Display);

    private enum HostDecision { Include, Custom, Skip }

    private sealed record HostOption(HostDecision Decision, string Display);
}

/// <summary>
/// Controls how Docker Compose <c>npm.*</c> labels are interpreted.
/// </summary>
public enum LabelMode {
    /// <summary>Use labels when present; fall back to interactive prompts otherwise.</summary>
    Auto,
    /// <summary>Every ported service must have an <c>npm.host</c> label; error if missing.</summary>
    Require,
    /// <summary>Ignore labels entirely; always use interactive prompts.</summary>
    Ignore
}
