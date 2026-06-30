using NginxProxy.Sdk;
using NpmCertificate = NginxProxy.Sdk.Nginx.Certificates.Certificates;
using Shiron.ComposeToNginx.Cli.Services;

namespace Shiron.ComposeToNginx.Cli.Services.Impl;

/// <inheritdoc/>
public sealed class CertificateResolver : ICertificateResolver {
    /// <inheritdoc/>
    public async Task<IReadOnlyList<NpmCertificateInfo>> FetchAsync(NginxProxySdk sdk, CancellationToken cancellationToken = default) {
        List<NpmCertificate> certs;
        try {
            certs = await sdk.Nginx.Certificates.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
        } catch (Exception ex) {
            throw new InvalidOperationException($"Could not load certificates from NGINX Proxy Manager: {ex.Message}", ex);
        }

        return certs.Select(ToInfo).ToList();
    }

    /// <inheritdoc/>
    public int? FindByReference(IReadOnlyList<NpmCertificateInfo> certificates, string reference) {
        var byName = certificates.FirstOrDefault(c =>
            string.Equals(c.NiceName, reference, StringComparison.OrdinalIgnoreCase));
        if (byName?.Id is int nameId) return nameId;

        return FindByDomain(certificates, reference);
    }

    /// <inheritdoc/>
    public int? FindByDomain(IReadOnlyList<NpmCertificateInfo> certificates, string domain) {
        var exact = certificates.FirstOrDefault(c =>
            c.DomainNames.Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase)));
        if (exact?.Id is int exactId) return exactId;

        var wildcard = certificates.FirstOrDefault(c =>
            c.DomainNames.Any(d => IsWildcardMatch(d, domain)));
        return wildcard?.Id;
    }

    private static bool IsWildcardMatch(string pattern, string domain) {
        if (!pattern.StartsWith("*.", StringComparison.Ordinal)) return false;
        var suffix = pattern[1..];
        return domain.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    private static NpmCertificateInfo ToInfo(NpmCertificate cert) =>
        new(
            cert.Id ?? 0,
            cert.NiceName,
            cert.DomainNames ?? [],
            cert.Provider
        );
}
