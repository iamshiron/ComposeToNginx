using Shiron.ComposeToNginx.Cli.Services;
using Shiron.ComposeToNginx.Core.Npm;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace Shiron.ComposeToNginx.Cli.Commands.Hosts;

[Description("List the proxy hosts currently configured in NGINX Proxy Manager.")]
public sealed class AsyncListHostsCommand(INpmClientFactory clientFactory, IAnsiConsole console) : AsyncCommand<AsyncListHostsCommand.Settings> {
    public sealed class Settings : NpmConnectionSettings;

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken) {
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

        if (hosts.Count == 0) {
            console.MarkupLine("[yellow]No proxy hosts found.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Proxy Hosts[/]");
        table.AddColumn(new TableColumn("ID").Centered());
        table.AddColumn("Domains");
        table.AddColumn("Forward");
        table.AddColumn(new TableColumn("SSL").Centered());
        table.AddColumn(new TableColumn("Status").Centered());

        foreach (var host in hosts) {
            var domains = host.DomainNames is { Count: > 0 } names
                ? string.Join(", ", names)
                : "[grey]-[/]";

            var scheme = host.ForwardScheme ?? "http";
            var forwardHost = Markup.Escape(host.ForwardHost ?? "?");
            var forwardPort = host.ForwardPort?.ToString() ?? "?";
            var forward = $"{scheme}://{forwardHost}:{forwardPort}";

            var ssl = host.SslForced is true ? "[green]Forced[/]" : "[grey]Off[/]";
            var status = host.Enabled is true ? "[green]Enabled[/]" : "[red]Disabled[/]";
            var id = host.Id?.ToString() ?? "?";

            table.AddRow($"[blue]{id}[/]", domains, Markup.Escape(forward), ssl, status);
        }

        console.Write(table);
        return 0;
    }
}
