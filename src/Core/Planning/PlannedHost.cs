using Shiron.ComposeToNginx.Core.Labels;

namespace Shiron.ComposeToNginx.Core.Planning;

/// <summary>
/// A single proxy host that ComposeToNginx intends to create (or recreate) in
/// NGINX Proxy Manager. Built either from <c>npm.*</c> labels or from an
/// interactive prompt. <see cref="OverwritesHostId"/> is set when an existing
/// host with the same domain signature must be deleted first.
/// </summary>
public sealed record PlannedHost(
    string Service,
    IReadOnlyList<string> Domains,
    string ForwardHost,
    int ForwardPort,
    string ForwardScheme,
    bool Ssl,
    int? CertificateId,
    bool ForceSsl,
    bool Http2,
    bool Websocket,
    bool BlockExploits,
    bool Caching,
    bool Hsts,
    bool Enabled,
    int? OverwritesHostId
) {
    /// <summary>Builds a <see cref="PlannedHost"/> from a parsed label config.</summary>
    public static PlannedHost FromLabelConfig(HostLabelConfig cfg, int? certificateId, int? overwritesHostId) =>
        new(
            cfg.Service,
            cfg.Domains,
            cfg.ForwardHost,
            cfg.ForwardPort,
            cfg.ForwardScheme,
            cfg.Ssl,
            certificateId,
            cfg.ForceSsl,
            cfg.Http2,
            cfg.Websocket,
            cfg.BlockExploits,
            cfg.Caching,
            cfg.Hsts,
            cfg.Enabled,
            overwritesHostId
        );

    /// <summary>
    /// Builds a <see cref="PlannedHost"/> from interactive input. SSL-dependent
    /// toggles (<c>ForceSsl</c>/<c>Http2</c>) only take effect when a
    /// certificate is attached, mirroring NPM's behaviour.
    /// </summary>
    public static PlannedHost ForInteractive(
        string service,
        IReadOnlyList<string> domains,
        string forwardHost,
        int forwardPort,
        bool ssl,
        int? certificateId,
        int? overwritesHostId
    ) {
        var hasCert = ssl && certificateId is not null;
        return new PlannedHost(
            service,
            domains,
            forwardHost,
            forwardPort,
            "http",
            ssl,
            certificateId,
            hasCert,
            hasCert,
            true,
            true,
            false,
            false,
            true,
            overwritesHostId
        );
    }
}
