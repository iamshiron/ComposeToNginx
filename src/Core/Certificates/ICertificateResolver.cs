using Shiron.ComposeToNginx.Core.Npm;

namespace Shiron.ComposeToNginx.Core.Certificates;

/// <summary>
/// Resolves NPM certificates by nice-name or by domain coverage (including
/// wildcard matching). Used to map <c>npm.cert</c> label values to certificate
/// IDs, and to auto-derive a certificate from a host's domain when
/// <c>npm.cert</c> is omitted.
/// </summary>
public interface ICertificateResolver {
    /// <summary>
    /// Resolves <paramref name="reference"/> to a certificate ID. The reference
    /// may be a certificate nice-name or a domain covered by the certificate.
    /// Exact nice-name matches take priority; then exact domain matches; then
    /// wildcard domain matches.
    /// </summary>
    /// <returns>The certificate ID, or <c>null</c> if not found.</returns>
    int? FindByReference(IReadOnlyList<NpmCertificateInfo> certificates, string reference);

    /// <summary>
    /// Finds a certificate whose domains cover <paramref name="domain"/>,
    /// including wildcard patterns (e.g. <c>*.example.com</c>). Exact domain
    /// matches take priority over wildcard matches.
    /// </summary>
    /// <returns>The certificate ID, or <c>null</c> if not found.</returns>
    int? FindByDomain(IReadOnlyList<NpmCertificateInfo> certificates, string domain);
}
