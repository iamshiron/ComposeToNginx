using NginxProxy.Sdk;
using NginxProxy.Sdk.Nginx.ProxyHosts;
using NpmCertificate = NginxProxy.Sdk.Nginx.Certificates.Certificates;
using Shiron.ComposeToNginx.Core.Npm;

namespace Shiron.ComposeToNginx.Cli.Services.Impl;

/// <summary>
/// Adapter that implements the domain <see cref="INpmClient"/> port over the
/// generated Kiota <see cref="NginxProxySdk"/>, translating to and from the
/// SDK-independent DTOs in <see cref="Shiron.ComposeToNginx.Core.Npm"/>.
/// </summary>
internal sealed class NpmClientAdapter(NginxProxySdk sdk) : INpmClient {
    /// <inheritdoc/>
    public async Task<IReadOnlyList<NpmCertificateInfo>> GetCertificatesAsync(CancellationToken cancellationToken = default) {
        var certs = await sdk.Nginx.Certificates.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
        return certs.Select(ToInfo).ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<NpmProxyHostInfo>> GetProxyHostsAsync(CancellationToken cancellationToken = default) {
        var hosts = await sdk.Nginx.ProxyHosts.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
        return hosts.Select(ToInfo).ToList();
    }

    /// <inheritdoc/>
    public async Task<int?> CreateProxyHostAsync(ProxyHostCreateRequest request, CancellationToken cancellationToken = default) {
        var body = ToRequestBody(request);
        var response = await sdk.Nginx.ProxyHosts.PostAsProxyHostsPostResponseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        return response?.Id;
    }

    /// <inheritdoc/>
    public async Task DeleteProxyHostAsync(int id, CancellationToken cancellationToken = default) {
        await sdk.Nginx.ProxyHosts[id].DeleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static NpmCertificateInfo ToInfo(NpmCertificate cert) =>
        new(
            cert.Id ?? 0,
            cert.NiceName,
            cert.DomainNames ?? [],
            cert.Provider,
            cert.ExpiresOn
        );

    private static NpmProxyHostInfo ToInfo(ProxyHosts host) =>
        new(
            host.Id,
            host.DomainNames ?? [],
            host.ForwardHost,
            host.ForwardPort,
            host.ForwardScheme?.ToString().ToLowerInvariant(),
            host.CertificateId?.Integer,
            host.SslForced,
            host.Enabled
        );

    private static ProxyHostsPostRequestBody ToRequestBody(ProxyHostCreateRequest request) {
        var scheme = request.ForwardScheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            ? ProxyHostsPostRequestBody_forward_scheme.Https
            : ProxyHostsPostRequestBody_forward_scheme.Http;

        return new ProxyHostsPostRequestBody {
            DomainNames = request.DomainNames.ToList(),
            ForwardScheme = scheme,
            ForwardHost = request.ForwardHost,
            ForwardPort = request.ForwardPort,
            CertificateId = request.CertificateId is null ? null : new() { Integer = request.CertificateId },
            SslForced = request.SslForced,
            Http2Support = request.Http2Support,
            HstsEnabled = request.HstsEnabled,
            BlockExploits = request.BlockExploits,
            CachingEnabled = request.CachingEnabled,
            AllowWebsocketUpgrade = request.AllowWebsocketUpgrade,
            Enabled = request.Enabled,
        };
    }
}
