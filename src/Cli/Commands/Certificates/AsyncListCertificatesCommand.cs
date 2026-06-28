using NginxProxy.Sdk;
using NpmCertificate = NginxProxy.Sdk.Nginx.Certificates.Certificates;
using Shiron.ComposeToNginx.Cli.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Globalization;

namespace Shiron.ComposeToNginx.Cli.Commands.Certificates;

[Description("List the certificates currently registered in NGINX Proxy Manager.")]
public sealed class AsyncListCertificatesCommand(INginxProxySdkFactory sdkFactory, IAnsiConsole console) : AsyncCommand<AsyncListCertificatesCommand.Settings> {
    private const int ExpiryWarningDays = 30;

    public sealed class Settings : NpmConnectionSettings;

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

        List<NpmCertificate> certificates;
        try {
            certificates = await sdk.Nginx.Certificates.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
        } catch (Exception ex) {
            return console.WriteError("Failed to fetch certificates", ex);
        }

        if (certificates.Count == 0) {
            console.MarkupLine("[yellow]No certificates found.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Certificates[/]");
        table.AddColumn(new TableColumn("ID").Centered());
        table.AddColumn("Provider");
        table.AddColumn("Domains");
        table.AddColumn("Expires");
        table.AddColumn(new TableColumn("Status").Centered());

        foreach (var cert in certificates) {
            var id = cert.Id?.ToString() ?? "?";
            var provider = Markup.Escape(cert.Provider ?? "-");
            var domains = cert.DomainNames is { Count: > 0 } names
                ? Markup.Escape(string.Join(", ", names))
                : "[grey]-[/]";

            var (expires, status) = FormatExpiry(cert.ExpiresOn);
            table.AddRow($"[blue]{id}[/]", provider, domains, expires, status);
        }

        console.Write(table);
        return 0;
    }

    private static (string Display, string Status) FormatExpiry(string? expiresOn) {
        if (expiresOn is null
            || !DateTime.TryParse(expiresOn, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expiry)) {
            return ("[grey]-[/]", "[grey]Unknown[/]");
        }

        var display = Markup.Escape(expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        if (expiry <= DateTime.UtcNow) return (display, "[red]Expired[/]");

        var remaining = expiry - DateTime.UtcNow;
        if (remaining.TotalDays <= ExpiryWarningDays) return (display, $"[yellow]Expires in {remaining.Days}d[/]");

        return (display, "[green]Valid[/]");
    }
}
