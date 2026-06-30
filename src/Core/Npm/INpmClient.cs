namespace Shiron.ComposeToNginx.Core.Npm;

/// <summary>
/// The domain-level port for talking to NGINX Proxy Manager. Core depends only
/// on this abstraction; the CLI provides an adapter backed by the generated
/// Kiota <c>NginxProxySdk</c>.
/// </summary>
public interface INpmClient {
    /// <summary>Lists every certificate registered in NPM.</summary>
    Task<IReadOnlyList<NpmCertificateInfo>> GetCertificatesAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists every proxy host currently configured in NPM.</summary>
    Task<IReadOnlyList<NpmProxyHostInfo>> GetProxyHostsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a proxy host from <paramref name="request"/> and returns the new
    /// host's id, or <c>null</c> if the server did not return one.
    /// </summary>
    Task<int?> CreateProxyHostAsync(ProxyHostCreateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes the proxy host with the given <paramref name="id"/>.</summary>
    Task DeleteProxyHostAsync(int id, CancellationToken cancellationToken = default);
}
