using Shiron.Lib.DockerUtils.Model;
using System.Text.RegularExpressions;

namespace Shiron.ComposeToNginx.Cli.Services;

/// <summary>
/// Parses Docker Compose <c>npm.*</c> labels on a <see cref="Service"/> into a
/// <see cref="HostLabelConfig"/>, applying defaults and validating values.
/// </summary>
public static class LabelConfigParser {
    internal const string HostLabel = "npm.host";
    internal const string AliasPrefix = "npm.alias.";
    internal const string SslLabel = "npm.ssl";
    internal const string CertLabel = "npm.cert";
    internal const string ForceSslLabel = "npm.force-ssl";
    internal const string Http2Label = "npm.http2";
    internal const string WebsocketLabel = "npm.websocket";
    internal const string BlockExploitsLabel = "npm.block-exploits";
    internal const string CachingLabel = "npm.caching";
    internal const string HstsLabel = "npm.hsts";
    internal const string SchemeLabel = "npm.scheme";
    internal const string EnabledLabel = "npm.enabled";
    internal const string ForwardHostLabel = "npm.forward-host";
    internal const string ForwardPortLabel = "npm.forward-port";

    private const int MinPort = 1;
    private const int MaxPort = 65535;

    private static readonly Regex HostnameRegex = new(
        @"^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)
    );

    /// <summary>
    /// Parses the labels on <paramref name="service"/> into a
    /// <see cref="HostLabelConfig"/>.
    /// </summary>
    /// <returns>
    /// A fully populated config, or <c>null</c> when the service has no
    /// <c>npm.host</c> label (i.e. it is not managed by ComposeToNginx).
    /// </returns>
    /// <exception cref="LabelConfigException">Thrown for malformed label values.</exception>
    public static HostLabelConfig? TryParse(Service service) {
        var labels = service.Labels;

        if (!labels.TryGetValue(HostLabel, out var hostValue) || string.IsNullOrWhiteSpace(hostValue))
            return null;

        var domains = ParseDomains(hostValue, labels);
        if (domains.Count == 0)
            throw new LabelConfigException(service.Name, HostLabel, "must specify at least one domain.");

        foreach (var domain in domains) {
            if (!HostnameRegex.IsMatch(domain))
                throw new LabelConfigException(service.Name, HostLabel, $"'{domain}' is not a valid hostname.");
        }

        var forwardHost = labels.TryGetValue(ForwardHostLabel, out var fh) && !string.IsNullOrWhiteSpace(fh)
            ? fh.Trim()
            : (service.ContainerName ?? service.Name);

        var forwardPort = labels.TryGetValue(ForwardPortLabel, out var fp) && !string.IsNullOrWhiteSpace(fp)
            ? ParsePort(service.Name, ForwardPortLabel, fp)
            : DefaultForwardPort(service);

        var ssl = GetBool(service.Name, labels, SslLabel, defaultValue: false);
        var certificate = labels.TryGetValue(CertLabel, out var cert) && !string.IsNullOrWhiteSpace(cert)
            ? cert.Trim()
            : null;
        var forceSsl = GetBool(service.Name, labels, ForceSslLabel, defaultValue: ssl);
        var http2 = GetBool(service.Name, labels, Http2Label, defaultValue: ssl);
        var websocket = GetBool(service.Name, labels, WebsocketLabel, defaultValue: true);
        var blockExploits = GetBool(service.Name, labels, BlockExploitsLabel, defaultValue: true);
        var caching = GetBool(service.Name, labels, CachingLabel, defaultValue: false);
        var hsts = GetBool(service.Name, labels, HstsLabel, defaultValue: false);
        var scheme = GetScheme(service.Name, labels);
        var enabled = GetBool(service.Name, labels, EnabledLabel, defaultValue: true);

        return new HostLabelConfig(
            service.Name,
            domains,
            forwardHost,
            forwardPort,
            scheme,
            ssl,
            certificate,
            forceSsl,
            http2,
            websocket,
            blockExploits,
            caching,
            hsts,
            enabled
        );
    }

    private static List<string> ParseDomains(string hostValue, IReadOnlyDictionary<string, string> labels) {
        var domains = hostValue
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var (key, value) in labels) {
            if (!key.StartsWith(AliasPrefix, StringComparison.Ordinal)) continue;
            if (string.IsNullOrWhiteSpace(value)) continue;
            var alias = value.Trim();
            if (!domains.Contains(alias, StringComparer.OrdinalIgnoreCase))
                domains.Add(alias);
        }

        return domains;
    }

    private static int DefaultForwardPort(Service service) {
        foreach (var port in service.Ports) {
            if (int.TryParse(port.HostPort, out var p) && p is >= MinPort and <= MaxPort)
                return p;
        }
        throw new LabelConfigException(
            service.Name, ForwardPortLabel,
            "no usable single numeric published port found; specify npm.forward-port explicitly."
        );
    }

    private static int ParsePort(string serviceName, string label, string value) {
        if (!int.TryParse(value.Trim(), out var port) || port is < MinPort or > MaxPort)
            throw new LabelConfigException(serviceName, label, $"'{value}' is not a valid port (1–65535).");
        return port;
    }

    private static string GetScheme(string serviceName, IReadOnlyDictionary<string, string> labels) {
        if (!labels.TryGetValue(SchemeLabel, out var raw) || string.IsNullOrWhiteSpace(raw))
            return "http";
        var scheme = raw.Trim().ToLowerInvariant();
        if (scheme is not ("http" or "https"))
            throw new LabelConfigException(serviceName, SchemeLabel, $"'{raw}' is not valid; must be 'http' or 'https'.");
        return scheme;
    }

    private static bool GetBool(string serviceName, IReadOnlyDictionary<string, string> labels, string label, bool defaultValue) {
        if (!labels.TryGetValue(label, out var value) || string.IsNullOrWhiteSpace(value))
            return defaultValue;
        return ParseBool(serviceName, label, value);
    }

    private static bool ParseBool(string serviceName, string label, string value) {
        return value.Trim().ToLowerInvariant() switch {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => throw new LabelConfigException(serviceName, label, $"'{value}' is not a valid boolean."),
        };
    }
}
