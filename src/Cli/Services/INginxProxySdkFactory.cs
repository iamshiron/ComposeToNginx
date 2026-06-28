using NginxProxy.Sdk;

namespace Shiron.ComposeToNginx.Cli.Services;

/// <summary>
/// Creates an authorized <see cref="NginxProxySdk"/> instance for the given
/// connection options. Each command that interacts with NGINX Proxy Manager
/// can request an authenticated client through this factory.
/// </summary>
public interface INginxProxySdkFactory {
    /// <summary>
    /// Authenticates against NGINX Proxy Manager and returns a client whose
    /// requests are authorized with the resulting JWT bearer token.
    /// </summary>
    Task<NginxProxySdk> CreateAsync(NpmConnectionOptions options, CancellationToken cancellationToken = default);
}
