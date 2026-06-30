using Shiron.ComposeToNginx.Cli.Services;
using Shiron.ComposeToNginx.Core.Labels;
using Shiron.ComposeToNginx.Core.Npm;
using Shiron.ComposeToNginx.Core.Planning;
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
    INpmClientFactory clientFactory,
    HostPlanner hostPlanner,
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
        var nonInteractive = settings.IsNonInteractive;
        var cat = hostPlanner.Categorise(services, settings.LabelMode);

        foreach (var service in cat.SkippedNoPort)
            console.MarkupLine($"  [grey]Skip[/] [bold]{Markup.Escape(service.Name)}[/][grey] — no exposed ports.[/]");

        if (cat.Errors.Count > 0) {
            console.MarkupLine("[red]Aborting: invalid npm.* labels detected.[/]");
            foreach (var ex in cat.Errors)
                console.MarkupLine($"  [red]•[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        if (settings.LabelMode == LabelMode.Require && cat.UnmanagedWithPort.Count > 0) {
            console.MarkupLine("[red]Aborting: --label-mode=require but the following services have ports but no npm.host label:[/]");
            foreach (var s in cat.UnmanagedWithPort)
                console.MarkupLine($"  [red]•[/] [bold]{Markup.Escape(s.Name)}[/]");
            return 1;
        }
        var anyLabelled = cat.Managed.Count > 0;
        var hasUnmanaged = cat.UnmanagedWithPort.Count > 0;

        console.MarkupLine("[bold]Generate proxy hosts from a Docker Compose file[/]");

        var interactiveFallback = ResolveInteractiveFallback(
            settings.LabelMode, nonInteractive, hasUnmanaged, anyLabelled, cat.UnmanagedWithPort.Count);

        if (!interactiveFallback && anyLabelled && !hasUnmanaged)
            console.MarkupLine("[grey]All services are label-driven — no prompts.[/]");
        console.WriteLine();

        // ── 3. Connect + fetch reference data ─────────────────────────────
        var anySsl = cat.Managed.Any(l => l.Ssl) || interactiveFallback;
        var clientRequired = !settings.DryRun || anySsl;

        var client = await TryCreateClientAsync(settings, cancellationToken, required: clientRequired).ConfigureAwait(false);
        if (client is null && clientRequired) return 1;

        IReadOnlyList<NpmCertificateInfo> certs = [];
        if (anySsl && client is not null) {
            try {
                certs = await client.GetCertificatesAsync(cancellationToken).ConfigureAwait(false);
            } catch (Exception ex) {
                if (cat.Managed.Any(l => l.Ssl)) {
                    return console.WriteError("Failed to load certificates", ex);
                }
                console.MarkupLine($"[yellow]Could not load certificates ({Markup.Escape(ex.Message)}); continuing without selection.[/]");
            }
        }

        var existingIndex = await LoadExistingHostIndexAsync(client, cancellationToken).ConfigureAwait(false);

        // ── 4. Accumulate (transaction) ───────────────────────────────────
        var planned = new List<PlannedHost>();

        // 4a. Label-driven hosts
        var enabledManaged = new List<HostLabelConfig>();
        foreach (var cfg in cat.Managed) {
            if (!cfg.Enabled) {
                console.MarkupLine($"  [grey]Skip[/] [bold]{Markup.Escape(cfg.Service)}[/][grey] — npm.enabled=false.[/]");
                continue;
            }
            enabledManaged.Add(cfg);
        }

        var labelPlan = hostPlanner.PlanLabelled(enabledManaged, certs, existingIndex);
        planned.AddRange(labelPlan.Planned);

        // 4b. Unlabelled services: prompt interactively, or skip.
        InteractiveDefaults? defaults = null;
        if (interactiveFallback) {
            if (anyLabelled)
                console.MarkupLine($"[grey]{cat.UnmanagedWithPort.Count} service(s) lack npm.* labels — entering interactive mode for those.[/]");
            else if (settings.LabelMode == LabelMode.Ignore)
                console.MarkupLine("[grey]Ignoring labels — entering interactive mode for all ported services.[/]");
            else
                console.MarkupLine("[grey]No services carry npm.* labels — entering interactive mode.[/]");
            console.WriteLine();

            defaults = PromptDefaults(certs);
            foreach (var service in cat.UnmanagedWithPort) {
                var exposed = service.Ports[0];
                if (!int.TryParse(exposed.HostPort, out var forwardPort) || forwardPort is < MinPort or > MaxPort) {
                    console.MarkupLine($"  [grey]Skip[/] [bold]{Markup.Escape(service.Name)}[/][grey] — exposed port '[yellow]{Markup.Escape(exposed.HostPort)}[/][grey]' is not a single numeric port.[/]");
                    continue;
                }

                var plannedHost = PromptForService(service, forwardPort, defaults, existingIndex);
                if (plannedHost is not null)
                    planned.Add(plannedHost);
            }
        } else {
            foreach (var service in cat.UnmanagedWithPort) {
                var reason = nonInteractive ? "non-interactive mode" : "skipped";
                console.MarkupLine($"  [grey]Skip[/] [bold]{Markup.Escape(service.Name)}[/][grey] — no npm.host label ({reason}).[/]");
            }
        }

        // ── 5. Abort on accumulation errors ───────────────────────────────
        if (labelPlan.Errors.Count > 0) {
            console.MarkupLine("[red]Aborting: configuration errors detected during planning.[/]");
            foreach (var e in labelPlan.Errors)
                console.MarkupLine($"  [red]•[/] {Markup.Escape(e)}");
            return 1;
        }

        if (planned.Count == 0) {
            console.MarkupLine("[yellow]No proxy hosts to create.[/]");
            return 0;
        }

        // ── 6. Detect no-op (idempotency) ────────────────────────────────
        var toApply = hostPlanner.WithoutUpToDate(planned, existingIndex);
        var inSync = planned.Count - toApply.Count;

        if (toApply.Count == 0) {
            console.WriteLine();
            console.MarkupLine($"[green]All {planned.Count} proxy host(s) are already in sync — nothing to do.[/]");
            return 0;
        }

        if (inSync > 0)
            console.MarkupLine($"[grey]{inSync} host(s) already in sync — skipping.[/]");

        // ── 7. Present ────────────────────────────────────────────────────
        console.WriteLine();
        RenderOverview(toApply);

        // ── 8. Dry-run exit ───────────────────────────────────────────────
        console.WriteLine();
        if (settings.DryRun) {
            console.MarkupLine($"[green]Dry run complete.[/] [grey]{toApply.Count} host(s) would be created. No changes were made.[/]");
            return 0;
        }

        if (client is null) return 1;

        // ── 9. Confirm ────────────────────────────────────────────────────
        var toDelete = toApply.Where(p => p.OverwritesHostId is not null).ToList();
        var deleteSummary = toDelete.Count > 0 ? $", delete {toDelete.Count} conflicting" : "";

        if (!nonInteractive) {
            if (!console.Confirm($"Create {toApply.Count} proxy host(s){deleteSummary} in NGINX Proxy Manager?", defaultValue: false)) {
                console.MarkupLine("[yellow]Cancelled. No changes were made.[/]");
                return 0;
            }
        }

        // ── 10. Apply ─────────────────────────────────────────────────────
        return await ApplyAsync(client, toApply, toDelete, cancellationToken).ConfigureAwait(false);
    }

    // ── Interactive prompts ───────────────────────────────────────────────

    private bool ResolveInteractiveFallback(
        LabelMode labelMode, bool nonInteractive, bool hasUnmanaged, bool anyLabelled, int unmanagedCount
    ) {
        if (nonInteractive || !hasUnmanaged) return false;
        if (labelMode == LabelMode.Ignore) return true;
        return anyLabelled ? PromptSkipOrFill(unmanagedCount) : true;
    }

    private bool PromptSkipOrFill(int unmanagedCount) {
        var choice = console.Prompt(
            new SelectionPrompt<FillDecision>()
                .Title($"{unmanagedCount} service(s) lack [blue]npm.*[/] labels. How should they be handled?")
                .AddChoices(FillDecision.Skip, FillDecision.Fill)
                .UseConverter(d => d switch {
                    FillDecision.Skip => "Skip them (label-driven only)",
                    FillDecision.Fill => "Fill them in interactively",
                    _ => d.ToString()
                }));
        return choice == FillDecision.Fill;
    }

    private sealed record InteractiveDefaults(
        string ForwardHost,
        string BaseDomain,
        bool UseSsl,
        int? CertificateId
    );

    private InteractiveDefaults PromptDefaults(IReadOnlyList<NpmCertificateInfo> certs) {
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
        var domainMatch = existingIndex.FindByDomain(defaultDomain);
        var portMatch = existingIndex.FindByPort(forwardPort);
        var identical = domainMatch is not null
            && domainMatch.IsIdentical(defaults.ForwardHost, forwardPort, defaults.UseSsl, defaults.CertificateId);

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
                var customExisting = existingIndex.FindByDomain(customDomain);
                if (customExisting is not null) {
                    console.MarkupLine($"    [yellow]'{Markup.Escape(customDomain)}' already exists as host #{customExisting.Id} — adding will overwrite it.[/]");
                }
                return PlannedHost.ForInteractive(service.Name, [customDomain], defaults.ForwardHost, forwardPort, defaults.UseSsl, defaults.CertificateId, customExisting?.Id);
            default:
                return PlannedHost.ForInteractive(service.Name, [defaultDomain], defaults.ForwardHost, forwardPort, defaults.UseSsl, defaults.CertificateId, overwriteTarget?.Id);
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

    // ── Client + reference data ──────────────────────────────────────────

    private async Task<INpmClient?> TryCreateClientAsync(Settings settings, CancellationToken cancellationToken, bool required) {
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
            return await clientFactory.CreateAsync(options, cancellationToken).ConfigureAwait(false);
        } catch (Exception ex) {
            if (required) {
                console.WriteError("Authentication failed", ex);
            } else {
                console.MarkupLine($"[yellow]Could not authenticate with NPM ({Markup.Escape(ex.Message)}); overwrite detection is disabled.[/]");
            }
            return null;
        }
    }

    private async Task<ExistingHostIndex> LoadExistingHostIndexAsync(INpmClient? client, CancellationToken cancellationToken) {
        if (client is null) return ExistingHostIndex.Empty;

        try {
            var hosts = await client.GetProxyHostsAsync(cancellationToken).ConfigureAwait(false);
            return ExistingHostIndex.From(hosts);
        } catch (Exception ex) {
            console.MarkupLine($"[yellow]Could not load existing hosts ({Markup.Escape(ex.Message)}); overwrite detection is disabled.[/]");
            return ExistingHostIndex.Empty;
        }
    }

    // ── Rendering ─────────────────────────────────────────────────────────

    private static ValidationResult ValidateHostname(string input) {
        var value = input.Trim();
        if (value.Length == 0) return ValidationResult.Error("[red]A value is required.[/]");
        return HostnameRegex.IsMatch(value)
            ? ValidationResult.Success()
            : ValidationResult.Error("[red]Enter a valid hostname (letters, digits, dots, hyphens).[/]");
    }

    private static string DisplayDomain(ExistingHost host) =>
        host.DomainNames is { Count: > 0 } names ? string.Join(", ", names) : host.Signature;

    private void RenderOverview(IReadOnlyList<PlannedHost> planned) {
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

    private async Task<int> ApplyAsync(INpmClient client, IReadOnlyList<PlannedHost> planned, IReadOnlyList<PlannedHost> toDelete, CancellationToken cancellationToken) {
        var deleted = 0;
        var deleteFailed = 0;

        if (toDelete.Count > 0) {
            console.MarkupLine($"[yellow]Deleting {toDelete.Count} conflicting host(s)…[/]");
            foreach (var h in toDelete) {
                if (h.OverwritesHostId is not int id) continue;
                try {
                    await client.DeleteProxyHostAsync(id, cancellationToken).ConfigureAwait(false);
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
                var request = ProxyHostRequestBuilder.Build(h);
                var responseId = await client.CreateProxyHostAsync(request, cancellationToken).ConfigureAwait(false);
                console.MarkupLine($"  [green]Created[/] [bold]{Markup.Escape(h.Service)}[/] → {Markup.Escape(string.Join(", ", h.Domains))} [grey](#{responseId})[/]");
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

    // ── Types ─────────────────────────────────────────────────────────────

    private sealed record CertificateChoice(int? Id, string Display);

    private enum HostDecision { Include, Custom, Skip }

    private enum FillDecision { Skip, Fill }

    private sealed record HostOption(HostDecision Decision, string Display);
}
