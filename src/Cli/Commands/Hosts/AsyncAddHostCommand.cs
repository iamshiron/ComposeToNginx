using NginxProxy.Sdk;
using NpmCertificate = NginxProxy.Sdk.Nginx.Certificates.Certificates;
using NginxProxy.Sdk.Nginx.ProxyHosts;
using Shiron.ComposeToNginx.Cli.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace Shiron.ComposeToNginx.Cli.Commands.Hosts;

[Description("Interactively create a new proxy host in NGINX Proxy Manager.")]
public sealed class AsyncAddHostCommand(INginxProxySdkFactory sdkFactory, IAnsiConsole console) : AsyncCommand<AsyncAddHostCommand.Settings> {
    private const int MinPort = 1;
    private const int MaxPort = 65535;

    public sealed class Settings : NpmMutationSettings;

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken) {
        NpmConnectionOptions options;
        try {
            options = settings.ToConnectionOptions();
        } catch (InvalidOperationException ex) {
            return console.WriteError("Configuration error", ex);
        }

        NginxProxySdk sdk;
        try {
            sdk = await sdkFactory.CreateAsync(options, cancellationToken).ConfigureAwait(false);
        } catch (Exception ex) {
            return console.WriteError("Authentication failed", ex);
        }

        console.MarkupLine("[bold]Create a new proxy host[/]");
        console.MarkupLine("[grey]Answer the prompts below; press Enter to accept the default where shown.[/]");
        console.WriteLine();

        var domains = PromptDomains();
        var scheme = PromptScheme();
        var forwardHost = PromptForwardHost();
        var forwardPort = PromptForwardPort();

        var certificates = await FetchCertificatesAsync(sdk, cancellationToken).ConfigureAwait(false);
        var certificateId = PromptCertificate(certificates);

        var sslForced = certificateId is not null && console.Confirm("Force SSL (redirect HTTP to HTTPS)?", defaultValue: true);
        var http2Support = certificateId is not null && console.Confirm("Enable HTTP/2 support?", defaultValue: true);
        var hstsEnabled = certificateId is not null && console.Confirm("Enable HSTS?", defaultValue: false);
        var blockExploits = console.Confirm("Block common exploits?", defaultValue: true);
        var cachingEnabled = console.Confirm("Enable asset caching?", defaultValue: false);
        var allowWebsocket = console.Confirm("Allow WebSocket upgrade?", defaultValue: true);
        var enabled = console.Confirm("Enable the host immediately?", defaultValue: true);

        RenderSummary(domains, scheme, forwardHost, forwardPort, certificateId, sslForced, http2Support, hstsEnabled, blockExploits, cachingEnabled, allowWebsocket, enabled);

        var body = new ProxyHostsPostRequestBody {
            DomainNames = domains,
            ForwardScheme = scheme,
            ForwardHost = forwardHost,
            ForwardPort = forwardPort,
            CertificateId = certificateId is null ? null : new() { Integer = certificateId },
            SslForced = sslForced,
            Http2Support = http2Support,
            HstsEnabled = hstsEnabled,
            BlockExploits = blockExploits,
            CachingEnabled = cachingEnabled,
            AllowWebsocketUpgrade = allowWebsocket,
            Enabled = enabled,
        };

        // ── Dry-run: preview only ──────────────────────────────────────
        if (settings.DryRun) {
            console.MarkupLine($"[green]Dry run complete.[/] [grey]1 host would be created. No changes were made.[/]");
            return 0;
        }

        // ── Confirm (skipped with --yes) ───────────────────────────────
        if (!settings.IsNonInteractive) {
            if (!console.Confirm("Create this proxy host?", defaultValue: true)) {
                console.MarkupLine("[yellow]Cancelled. No host was created.[/]");
                return 0;
            }
        }

        ProxyHostsPostResponse? created;
        try {
            created = await sdk.Nginx.ProxyHosts.PostAsProxyHostsPostResponseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        } catch (Exception ex) {
            return console.WriteError("Failed to create proxy host", ex);
        }

        console.MarkupLine($"[green]Proxy host created.[/] ID: [blue]{created?.Id?.ToString() ?? "?"}[/]");
        return 0;
    }

    private List<string> PromptDomains() {
        var input = console.Prompt(
            new TextPrompt<string>("Domain name(s) [grey](comma or space separated)[/]:")
                .Validate(i => SplitDomains(i).Count > 0
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Please enter at least one domain name.[/]"))
        );
        return SplitDomains(input);
    }

    private ProxyHostsPostRequestBody_forward_scheme PromptScheme() {
        var scheme = console.Prompt(
            new SelectionPrompt<string>()
                .Title("Forward scheme")
                .AddChoices("http", "https")
        );
        return scheme == "https" ? ProxyHostsPostRequestBody_forward_scheme.Https : ProxyHostsPostRequestBody_forward_scheme.Http;
    }

    private string PromptForwardHost() =>
        console.Prompt(
            new TextPrompt<string>("Forward host [grey](e.g. app, 172.17.0.1)[/]:")
                .Validate(h => !string.IsNullOrWhiteSpace(h)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]A forward host is required.[/]"))
        );

    private int PromptForwardPort() =>
        console.Prompt(
            new TextPrompt<int>("Forward port:")
                .Validate(p => p is >= MinPort and <= MaxPort
                    ? ValidationResult.Success()
                    : ValidationResult.Error($"[red]Port must be between {MinPort} and {MaxPort}.[/]"))
        );

    private async Task<List<NpmCertificate>> FetchCertificatesAsync(NginxProxySdk sdk, CancellationToken cancellationToken) {
        try {
            return await sdk.Nginx.Certificates.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
        } catch (Exception ex) {
            console.MarkupLine($"[yellow]Could not load certificates ({Markup.Escape(ex.Message)}). Continuing without certificate selection.[/]");
            return [];
        }
    }

    private int? PromptCertificate(List<NpmCertificate> certificates) {
        if (certificates.Count == 0) {
            console.MarkupLine("[grey]No certificates available — the host will be created without SSL.[/]");
            return null;
        }

        var choices = new List<CertificateChoice> { new(null, "None (no certificate)") };
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

    private void RenderSummary(
        List<string> domains, ProxyHostsPostRequestBody_forward_scheme scheme, string forwardHost, int forwardPort,
        int? certificateId, bool sslForced, bool http2Support, bool hstsEnabled, bool blockExploits, bool cachingEnabled, bool allowWebsocket, bool enabled
    ) {
        var schemeStr = scheme.ToString().ToLowerInvariant();
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(2))
            .AddColumn()
            .AddRow("[bold]Domains[/]", Markup.Escape(string.Join(", ", domains)))
            .AddRow("[bold]Forward[/]", Markup.Escape($"{schemeStr}://{forwardHost}:{forwardPort}"))
            .AddRow("[bold]Certificate[/]", certificateId is null ? "[grey]None[/]" : $"#{certificateId}")
            .AddRow("[bold]SSL forced[/]", sslForced ? "[green]Yes[/]" : "[grey]No[/]")
            .AddRow("[bold]HTTP/2[/]", http2Support ? "[green]Yes[/]" : "[grey]No[/]")
            .AddRow("[bold]HSTS[/]", hstsEnabled ? "[green]Yes[/]" : "[grey]No[/]")
            .AddRow("[bold]Block exploits[/]", blockExploits ? "[green]Yes[/]" : "[grey]No[/]")
            .AddRow("[bold]Caching[/]", cachingEnabled ? "[green]Yes[/]" : "[grey]No[/]")
            .AddRow("[bold]WebSocket[/]", allowWebsocket ? "[green]Yes[/]" : "[grey]No[/]")
            .AddRow("[bold]Enabled[/]", enabled ? "[green]Yes[/]" : "[grey]No[/]");

        console.Write(new Panel(grid).Header("[bold]Review proxy host[/]").Border(BoxBorder.Rounded));
    }

    private static List<string> SplitDomains(string input) =>
        input.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private sealed record CertificateChoice(int? Id, string Display);
}
