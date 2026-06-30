namespace Shiron.ComposeToNginx.Core.Npm;

/// <summary>
/// A fully-materialised request to create (or recreate) a proxy host in NGINX
/// Proxy Manager. Domain-level equivalent of the generated
/// <c>ProxyHostsPostRequestBody</c>; produced from a <c>PlannedHost</c>.
/// </summary>
public sealed record ProxyHostCreateRequest(
    IReadOnlyList<string> DomainNames,
    string ForwardScheme,
    string ForwardHost,
    int ForwardPort,
    int? CertificateId,
    bool SslForced,
    bool Http2Support,
    bool HstsEnabled,
    bool BlockExploits,
    bool CachingEnabled,
    bool AllowWebsocketUpgrade,
    bool Enabled
);
