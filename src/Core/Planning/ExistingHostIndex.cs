using Shiron.ComposeToNginx.Core.Npm;

namespace Shiron.ComposeToNginx.Core.Planning;

/// <summary>
/// A proxy host already present in NGINX Proxy Manager, indexed for overwrite
/// detection. <see cref="Signature"/> is the ordered, lower-cased join of its
/// domain names, so two hosts with the same domains compare equal regardless
/// of ordering or case.
/// </summary>
public sealed record ExistingHost(
    int? Id,
    string Signature,
    IReadOnlyList<string> DomainNames,
    string? ForwardHost,
    int? ForwardPort,
    int? CertificateId,
    bool? SslForced
) {
    /// <summary>
    /// Returns <c>true</c> when a planned host with the given forward target and
    /// SSL setup would be identical to this existing host (i.e. already in
    /// sync, no recreation needed).
    /// </summary>
    public bool IsIdentical(string forwardHost, int forwardPort, bool useSsl, int? certificateId) {
        if (!string.Equals(ForwardHost, forwardHost, StringComparison.OrdinalIgnoreCase)) return false;
        if (ForwardPort != forwardPort) return false;

        var plannedSslForced = useSsl && certificateId is not null;
        var existingSslForced = SslForced ?? false;
        return plannedSslForced == existingSslForced && CertificateId == certificateId;
    }
}

/// <summary>
/// A lookup structure over the existing proxy hosts in NPM, keyed by domain
/// signature and by forward port, for fast overwrite/conflict detection during
/// planning. Pure data: built once per command run from <see cref="INpmClient"/>.
/// </summary>
public sealed class ExistingHostIndex {
    private readonly IReadOnlyDictionary<string, ExistingHost> _byDomain;
    private readonly IReadOnlyDictionary<int, ExistingHost> _byPort;

    private ExistingHostIndex(IReadOnlyDictionary<string, ExistingHost> byDomain, IReadOnlyDictionary<int, ExistingHost> byPort) {
        _byDomain = byDomain;
        _byPort = byPort;
    }

    /// <summary>An empty index, used when existing hosts could not be loaded.</summary>
    public static ExistingHostIndex Empty { get; } = new(
        new Dictionary<string, ExistingHost>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<int, ExistingHost>()
    );

    /// <summary>Builds an index from NPM proxy host DTOs.</summary>
    public static ExistingHostIndex From(IEnumerable<NpmProxyHostInfo> hosts) {
        var byDomain = new Dictionary<string, ExistingHost>(StringComparer.OrdinalIgnoreCase);
        var byPort = new Dictionary<int, ExistingHost>();

        foreach (var host in hosts) {
            var sig = DomainSignature(host.DomainNames);
            var existing = new ExistingHost(
                host.Id,
                sig ?? "",
                host.DomainNames,
                host.ForwardHost,
                host.ForwardPort,
                host.CertificateId,
                host.SslForced
            );

            if (sig is not null && !byDomain.ContainsKey(sig)) byDomain[sig] = existing;
            if (host.ForwardPort is int port && !byPort.ContainsKey(port)) byPort[port] = existing;
        }

        return new ExistingHostIndex(byDomain, byPort);
    }

    /// <summary>Finds an existing host whose domain signature matches <paramref name="domains"/>.</summary>
    public ExistingHost? FindByDomain(IReadOnlyList<string> domains) {
        var sig = DomainSignature(domains);
        return sig is not null && _byDomain.TryGetValue(sig, out var existing) ? existing : null;
    }

    /// <summary>Finds an existing host whose domain signature matches the single <paramref name="domain"/>.</summary>
    public ExistingHost? FindByDomain(string domain) {
        var sig = DomainSignature([domain]);
        return sig is not null && _byDomain.TryGetValue(sig, out var existing) ? existing : null;
    }

    /// <summary>Finds an existing host forwarding to <paramref name="port"/>.</summary>
    public ExistingHost? FindByPort(int port) =>
        _byPort.TryGetValue(port, out var existing) ? existing : null;

    private static string? DomainSignature(IReadOnlyList<string>? domains) {
        if (domains is null || domains.Count == 0) return null;
        return string.Join(',', domains.Select(d => d.Trim().ToLowerInvariant()).OrderBy(d => d));
    }
}
