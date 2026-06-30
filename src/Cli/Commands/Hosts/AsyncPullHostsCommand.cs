using Shiron.ComposeToNginx.Cli.Services;
using Shiron.ComposeToNginx.Core.Labels;
using Shiron.ComposeToNginx.Core.Npm;
using Shiron.ComposeToNginx.Core.Planning;
using Shiron.Lib.DockerUtils;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace Shiron.ComposeToNginx.Cli.Commands.Hosts;

/// <summary>
/// Reads an existing Docker Compose file and the proxy hosts currently
/// configured in NGINX Proxy Manager, then backfills <c>npm.*</c> labels onto
/// each service from its matching host — turning a manually managed (or
/// previously pushed) setup into a label-driven one. The compose file is only
/// rewritten after an explicit review and confirmation.
/// </summary>
[Description("Backfill npm.* labels onto a Docker Compose file from existing NGINX Proxy Manager proxy hosts.")]
public sealed class AsyncPullHostsCommand(
    INpmClientFactory clientFactory,
    PullPlanner pullPlanner,
    IAnsiConsole console,
    IComposeReader composeReader,
    IComposeLabelWriter labelWriter
) : AsyncCommand<AsyncPullHostsCommand.Settings> {

    public sealed class Settings : NpmMutationSettings {
        [CommandArgument(0, "<file>")]
        [Description("Path to the docker-compose file to backfill with npm.* labels.")]
        public string? ComposeFile { get; init; }

        [CommandOption("--output")]
        [Description("Write the updated compose file here instead of overwriting the input. Lets you review before replacing the original.")]
        public string? Output { get; init; }

        [CommandOption("--cert-ref")]
        [Description("How to write npm.cert for SSL hosts: 'none' (omit, inferred on push — default) or 'nice-name' (write the certificate's name). Prompted when omitted in interactive mode.")]
        public string? CertRef { get; init; }
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

        IReadOnlyList<Lib.DockerUtils.Model.Service> services;
        try {
            services = composeReader.Read(yaml);
        } catch (ComposeReadException ex) {
            return console.WriteError("Failed to parse compose file", ex);
        }

        if (services.Count == 0) {
            console.MarkupLine("[yellow]No services found in the compose file.[/]");
            return 0;
        }

        CertReferenceMode? specifiedCertRef = null;
        if (settings.CertRef is not null) {
            if (!TryParseCertRef(settings.CertRef, out var parsed)) {
                return console.WriteError("Invalid --cert-ref", new ArgumentException(
                    $"'{settings.CertRef}' is not a valid cert reference mode. Use 'none' or 'nice-name'."));
            }
            specifiedCertRef = parsed;
        }

        // ── 2. Connect + fetch NPM state (required for a pull) ────────────
        NpmConnectionOptions options;
        try {
            options = settings.ToConnectionOptions();
        } catch (InvalidOperationException ex) {
            return console.WriteError("Configuration error", ex);
        }

        INpmClient client;
        try {
            client = await clientFactory.CreateAsync(options, cancellationToken).ConfigureAwait(false);
        } catch (Exception ex) {
            return console.WriteError("Authentication failed", ex);
        }

        IReadOnlyList<NpmProxyHostInfo> hosts;
        try {
            hosts = await client.GetProxyHostsAsync(cancellationToken).ConfigureAwait(false);
        } catch (Exception ex) {
            return console.WriteError("Failed to fetch proxy hosts", ex);
        }

        IReadOnlyList<NpmCertificateInfo> certs = [];
        try {
            certs = await client.GetCertificatesAsync(cancellationToken).ConfigureAwait(false);
        } catch (Exception ex) {
            console.MarkupLine($"[yellow]Could not load certificates ({Markup.Escape(ex.Message)}); npm.cert references will be omitted.[/]");
        }

        // ── 3. Plan (transaction) ─────────────────────────────────────────
        console.MarkupLine("[bold]Backfill npm.* labels from NGINX Proxy Manager[/]");
        console.WriteLine();

        var certRefMode = specifiedCertRef ?? CertReferenceMode.None;
        var plan = pullPlanner.Plan(services, hosts, certs, certRefMode);

        foreach (var skip in plan.Skipped)
            console.MarkupLine($"  [grey]Skip[/] [bold]{Markup.Escape(skip.Service)}[/][grey] — {ReasonText(skip.Reason)}.[/]");

        if (plan.Planned.Count == 0) {
            console.WriteLine();
            console.MarkupLine("[yellow]No labels to add — nothing to do.[/]");
            return 0;
        }

        // ── 4. Resolve cert-reference mode (interactive) ─────────────────
        if (specifiedCertRef is null
            && !settings.IsNonInteractive
            && plan.Planned.Any(p => p.Labels.ContainsKey("npm.ssl"))) {
            var choice = console.Prompt(
                new SelectionPrompt<CertReferenceMode>()
                    .Title("How should [blue]npm.cert[/] be written for SSL hosts?")
                    .AddChoices(CertReferenceMode.None, CertReferenceMode.NiceName)
                    .UseConverter(CertRefChoiceLabel));
            if (choice != certRefMode) {
                certRefMode = choice;
                plan = pullPlanner.Plan(services, hosts, certs, certRefMode);
            }
        }

        // ── 5. Present ─────────────────────────────────────────────────────
        console.WriteLine();
        RenderPlannedLabels(plan.Planned);

        var editsByService = plan.Planned.ToDictionary(p => p.Service, p => p.Labels);
        string updated;
        try {
            updated = labelWriter.ApplyLabels(yaml, editsByService);
        } catch (ArgumentException ex) {
            return console.WriteError("Failed to build updated compose file", ex);
        }

        console.WriteLine();
        console.Write(new Panel(updated.EscapeMarkup())
            .Header($"[bold]Resulting compose file[/]{(settings.Output is not null ? "" : "  [grey](in-place)[/]")}")
            .Border(BoxBorder.Rounded));

        // ── 6. Dry-run exit ───────────────────────────────────────────────
        if (settings.DryRun) {
            console.WriteLine();
            console.MarkupLine($"[green]Dry run complete.[/] [grey]{plan.Planned.Count} service(s) would be labelled. No changes were made.[/]");
            return 0;
        }

        // ── 7. Confirm ─────────────────────────────────────────────────────
        var destination = ResolveDestination(path, settings.Output);

        if (!settings.IsNonInteractive) {
            var where = destination == path ? "the compose file" : Markup.Escape(destination);
            if (!console.Confirm($"Write {plan.Planned.Count} label edit(s) to {where}?", defaultValue: false)) {
                console.MarkupLine("[yellow]Cancelled. No changes were made.[/]");
                return 0;
            }
        }

        // ── 8. Apply ───────────────────────────────────────────────────────
        try {
            await File.WriteAllTextAsync(destination, updated, cancellationToken).ConfigureAwait(false);
        } catch (Exception ex) {
            return console.WriteError($"Failed to write '{destination}'", ex);
        }

        console.MarkupLine($"[green]Done.[/] Wrote {plan.Planned.Count} label edit(s) to [bold]{Markup.Escape(destination)}[/].");
        return 0;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool TryParseCertRef(string? raw, out CertReferenceMode mode) {
        var norm = (raw ?? "").Trim().ToLowerInvariant().Replace("-", "").Replace("_", "");
        mode = norm switch {
            "none" => CertReferenceMode.None,
            "nicename" => CertReferenceMode.NiceName,
            _ => default
        };
        return norm is "none" or "nicename";
    }

    private static string CertRefChoiceLabel(CertReferenceMode mode) => mode switch {
        CertReferenceMode.None => "none — omit npm.cert (inferred from domain on push)",
        CertReferenceMode.NiceName => "nice-name — write the certificate's name",
        _ => mode.ToString()
    };

    private static string ResolveDestination(string inputPath, string? output) =>
        string.IsNullOrWhiteSpace(output) ? inputPath : output;

    private static string ReasonText(PullSkipReason reason) => reason switch {
        PullSkipReason.AlreadyManaged => "already managed (has npm.host)",
        PullSkipReason.NoPorts => "no published ports to match against",
        PullSkipReason.NoMatch => "no matching NPM proxy host",
        _ => reason.ToString()
    };

    private void RenderPlannedLabels(IReadOnlyList<PlannedLabelEdit> planned) {
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Planned label additions[/]");
        table.AddColumn("Service");
        table.AddColumn("From NPM host");
        table.AddColumn("Domains");
        table.AddColumn("Labels to add");

        foreach (var edit in planned) {
            var labels = string.Join("\n", edit.Labels.Select(kv => $"[blue]{Markup.Escape(kv.Key)}[/]: {Markup.Escape(kv.Value)}"));
            table.AddRow(
                Markup.Escape(edit.Service),
                $"#{edit.MatchedHostId}",
                Markup.Escape(edit.MatchedDomains),
                string.IsNullOrEmpty(labels) ? "[grey](none)[/]" : labels
            );
        }

        console.Write(table);
    }
}
