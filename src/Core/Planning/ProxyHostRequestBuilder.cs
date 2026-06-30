using Shiron.ComposeToNginx.Core.Npm;

namespace Shiron.ComposeToNginx.Core.Planning;

/// <summary>
/// Builds <see cref="ProxyHostCreateRequest"/>s from planned hosts. Centralises
/// the translation from the domain plan into an NPM mutation, including the
/// rule that SSL-dependent toggles only apply when a certificate is attached.
/// </summary>
public static class ProxyHostRequestBuilder {
    /// <summary>
    /// Converts <paramref name="host"/> into a create request. <c>SslForced</c>
    /// and <c>Http2Support</c> are gated on a certificate being present, matching
    /// NPM's effective behaviour.
    /// </summary>
    public static ProxyHostCreateRequest Build(PlannedHost host) {
        var hasCert = host.CertificateId is not null;

        return new ProxyHostCreateRequest(
            host.Domains,
            host.ForwardScheme,
            host.ForwardHost,
            host.ForwardPort,
            host.CertificateId,
            SslForced: hasCert && host.ForceSsl,
            Http2Support: hasCert && host.Http2,
            HstsEnabled: host.Hsts,
            BlockExploits: host.BlockExploits,
            CachingEnabled: host.Caching,
            AllowWebsocketUpgrade: host.Websocket,
            Enabled: host.Enabled
        );
    }
}
