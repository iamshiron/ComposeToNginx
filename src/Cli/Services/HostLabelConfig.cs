namespace Shiron.ComposeToNginx.Cli.Services;

/// <summary>
/// Parsed proxy-host intent declared via Docker Compose <c>npm.*</c> labels.
/// Produced by <see cref="LabelConfigParser"/>.
/// </summary>
/// <param name="Service">The Compose service name.</param>
/// <param name="Domains">Primary domain plus SANs (from <c>npm.host</c> and <c>npm.alias.&lt;n&gt;</c>).</param>
/// <param name="ForwardHost">Where NPM sends traffic (defaults to container/service name).</param>
/// <param name="ForwardPort">Forward port (defaults to first published port).</param>
/// <param name="ForwardScheme">Forward scheme — <c>http</c> or <c>https</c>.</param>
/// <param name="Ssl">Whether SSL is enabled.</param>
/// <param name="Certificate">Raw <c>npm.cert</c> reference (nice-name or domain), or <c>null</c> to auto-derive from <see cref="Domains"/>.</param>
/// <param name="ForceSsl">Force HTTPS redirect.</param>
/// <param name="Http2">HTTP/2 support.</param>
/// <param name="Websocket">Allow WebSocket upgrade.</param>
/// <param name="BlockExploits">Block common exploits.</param>
/// <param name="Caching">Enable asset caching.</param>
/// <param name="Hsts">Enable HSTS.</param>
/// <param name="Enabled">Host enabled flag.</param>
public sealed record HostLabelConfig(
    string Service,
    IReadOnlyList<string> Domains,
    string ForwardHost,
    int ForwardPort,
    string ForwardScheme,
    bool Ssl,
    string? Certificate,
    bool ForceSsl,
    bool Http2,
    bool Websocket,
    bool BlockExploits,
    bool Caching,
    bool Hsts,
    bool Enabled
);
