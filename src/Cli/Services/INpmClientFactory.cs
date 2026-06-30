using Shiron.ComposeToNginx.Core.Npm;

namespace Shiron.ComposeToNginx.Cli.Services;

/// <summary>
/// Creates an authenticated <see cref="INpmClient"/> for the given connection
/// options. Each command that interacts with NGINX Proxy Manager requests its
/// client through this factory.
/// </summary>
public interface INpmClientFactory {
    /// <summary>
    /// Authenticates against NGINX Proxy Manager and returns a client whose
    /// requests are authorized with the resulting JWT bearer token.
    /// </summary>
    Task<INpmClient> CreateAsync(NpmConnectionOptions options, CancellationToken cancellationToken = default);
}
