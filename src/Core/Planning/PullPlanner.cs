using System.Globalization;
using Shiron.ComposeToNginx.Core.Labels;
using Shiron.ComposeToNginx.Core.Npm;
using Shiron.Lib.DockerUtils.Model;

namespace Shiron.ComposeToNginx.Core.Planning;

/// <summary>
/// Why a Compose service was <em>not</em> turned into a
/// <see cref="PlannedLabelEdit"/> during a <c>hosts pull</c> run.
/// </summary>
public enum PullSkipReason {
    /// <summary>The service already carries an <c>npm.host</c> label (already managed).</summary>
    AlreadyManaged,
    /// <summary>The service exposes no published ports to match against.</summary>
    NoPorts,
    /// <summary>No existing NPM host forwards to any of the service's ports.</summary>
    NoMatch
}

/// <summary>A Compose service that was skipped during planning, with the reason.</summary>
public sealed record PullSkip(string Service, PullSkipReason Reason);

/// <summary>
/// Result of a <c>hosts pull</c> plan: the accumulated label edits plus the
/// services that were intentionally left untouched.
/// </summary>
public sealed record PullPlanResult(
    IReadOnlyList<PlannedLabelEdit> Planned,
    IReadOnlyList<PullSkip> Skipped
);

/// <summary>
/// Coordinates the pure planning side of a <c>hosts pull</c> run: matching each
/// Compose service to an existing NGINX Proxy Manager host (by forward port,
/// preferring a forward-host match), then deriving the minimal set of
/// <c>npm.*</c> labels that capture the host's configuration. The CLI layer
/// drives I/O, rendering and confirmation around this pure step.
/// </summary>
public sealed class PullPlanner {
    private const int MinPort = 1;
    private const int MaxPort = 65535;
    private const string DefaultScheme = "http";

    /// <summary>
    /// Matches <paramref name="services"/> against <paramref name="hosts"/> and,
    /// for each unmanaged service with a matching host, derives the
    /// <c>npm.*</c> labels that reproduce the host's configuration. Services
    /// that are already managed, expose no ports, or have no matching host are
    /// returned in <see cref="PullPlanResult.Skipped"/>.
    /// </summary>
    /// <param name="certificates">
    /// Existing NPM certificates, used to attach a human-readable
    /// <c>npm.cert</c> reference to SSL hosts. May be empty.
    /// </param>
    public PullPlanResult Plan(
        IReadOnlyList<Service> services,
        IReadOnlyList<NpmProxyHostInfo> hosts,
        IReadOnlyList<NpmCertificateInfo> certificates
    ) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(certificates);

        var byPort = IndexByPort(hosts);

        var planned = new List<PlannedLabelEdit>();
        var skipped = new List<PullSkip>();

        foreach (var service in services) {
            if (IsManaged(service)) {
                skipped.Add(new PullSkip(service.Name, PullSkipReason.AlreadyManaged));
                continue;
            }

            var match = FindMatch(service, byPort);
            if (match is null) {
                var reason = service.Ports.Length == 0 ? PullSkipReason.NoPorts : PullSkipReason.NoMatch;
                skipped.Add(new PullSkip(service.Name, reason));
                continue;
            }

            var labels = BuildLabels(service, match, certificates);
            planned.Add(new PlannedLabelEdit(
                service.Name,
                match.Id ?? 0,
                DisplayDomains(match.DomainNames),
                DisplayForward(match),
                labels
            ));
        }

        return new PullPlanResult(planned, skipped);
    }

    // ── Matching ────────────────────────────────────────────────────────────

    private static Dictionary<int, List<NpmProxyHostInfo>> IndexByPort(IReadOnlyList<NpmProxyHostInfo> hosts) {
        var index = new Dictionary<int, List<NpmProxyHostInfo>>();
        foreach (var host in hosts) {
            if (host.ForwardPort is not int port) continue;
            if (!index.TryGetValue(port, out var bucket))
                index[port] = bucket = new List<NpmProxyHostInfo>();
            bucket.Add(host);
        }
        return index;
    }

    private static NpmProxyHostInfo? FindMatch(Service service, IReadOnlyDictionary<int, List<NpmProxyHostInfo>> byPort) {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { service.Name };
        if (!string.IsNullOrWhiteSpace(service.ContainerName))
            identities.Add(service.ContainerName);

        foreach (var port in service.Ports) {
            if (!int.TryParse(port.HostPort, out var hostPort)) continue;
            if (hostPort is < MinPort or > MaxPort) continue;
            if (!byPort.TryGetValue(hostPort, out var candidates)) continue;

            // Prefer a candidate whose forward host is the service/container name.
            var byName = candidates.FirstOrDefault(c =>
                !string.IsNullOrWhiteSpace(c.ForwardHost) && identities.Contains(c.ForwardHost));
            return byName ?? candidates[0];
        }

        return null;
    }

    private static bool IsManaged(Service service) =>
        service.Labels.TryGetValue(LabelConfigParser.HostLabel, out var host)
        && !string.IsNullOrWhiteSpace(host);

    // ── Label derivation ────────────────────────────────────────────────────

    private static Dictionary<string, string> BuildLabels(
        Service service, NpmProxyHostInfo host, IReadOnlyList<NpmCertificateInfo> certificates
    ) {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);

        // npm.host — primary domain plus SANs. The parser splits on commas, so a
        // single comma-separated value reproduces the full domain set exactly.
        if (host.DomainNames is { Count: > 0 })
            labels[LabelConfigParser.HostLabel] = string.Join(",", host.DomainNames);

        // npm.forward-host — only when it differs from the default (container name ?? service name).
        var defaultForwardHost = service.ContainerName ?? service.Name;
        if (!string.IsNullOrWhiteSpace(host.ForwardHost)
            && !string.Equals(host.ForwardHost, defaultForwardHost, StringComparison.OrdinalIgnoreCase)) {
            labels[LabelConfigParser.ForwardHostLabel] = host.ForwardHost;
        }

        // npm.forward-port — only when it differs from the first numeric published port.
        if (host.ForwardPort is int forwardPort) {
            var defaultPort = FirstNumericPublishedPort(service);
            if (defaultPort is null || forwardPort != defaultPort)
                labels[LabelConfigParser.ForwardPortLabel] = forwardPort.ToString(CultureInfo.InvariantCulture);
        }

        // npm.scheme — only when non-default.
        if (string.Equals(host.ForwardScheme, "https", StringComparison.OrdinalIgnoreCase))
            labels[LabelConfigParser.SchemeLabel] = "https";

        // npm.ssl + npm.cert — when a certificate is attached. force-ssl defaults
        // to the ssl value, so it is only emitted when explicitly disabled.
        if (host.CertificateId is int certId) {
            labels[LabelConfigParser.SslLabel] = "true";
            if (host.SslForced is false)
                labels[LabelConfigParser.ForceSslLabel] = "false";

            var cert = certificates.FirstOrDefault(c => c.Id == certId);
            if (!string.IsNullOrWhiteSpace(cert?.NiceName))
                labels[LabelConfigParser.CertLabel] = cert!.NiceName;
        }

        // npm.enabled — defaults to true, so only emit when disabled.
        if (host.Enabled is false)
            labels[LabelConfigParser.EnabledLabel] = "false";

        return labels;
    }

    private static int? FirstNumericPublishedPort(Service service) {
        foreach (var port in service.Ports) {
            if (int.TryParse(port.HostPort, out var p) && p is >= MinPort and <= MaxPort)
                return p;
        }
        return null;
    }

    // ── Display helpers ─────────────────────────────────────────────────────

    private static string DisplayDomains(IReadOnlyList<string> domains) =>
        domains is { Count: > 0 } ? string.Join(", ", domains) : "(none)";

    private static string DisplayForward(NpmProxyHostInfo host) {
        var scheme = string.IsNullOrWhiteSpace(host.ForwardScheme) ? DefaultScheme : host.ForwardScheme;
        var forwardHost = string.IsNullOrWhiteSpace(host.ForwardHost) ? "?" : host.ForwardHost;
        var port = host.ForwardPort?.ToString(CultureInfo.InvariantCulture) ?? "?";
        return $"{scheme}://{forwardHost}:{port}";
    }
}
