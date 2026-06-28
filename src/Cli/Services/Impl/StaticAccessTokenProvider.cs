using Microsoft.Kiota.Abstractions.Authentication;

namespace Shiron.ComposeToNginx.Cli.Services.Impl;

/// <summary>
/// An <see cref="IAccessTokenProvider"/> that returns a pre-acquired JWT.
/// Used once the factory has authenticated, so every subsequent request from
/// the <see cref="NginxProxy.Sdk.NginxProxySdk"/> carries the bearer token.
/// </summary>
public sealed class StaticAccessTokenProvider(string accessToken) : IAccessTokenProvider {
    private static readonly AllowedHostsValidator AllHosts = new(["*"]);

    public AllowedHostsValidator AllowedHostsValidator => AllHosts;

    public Task<string> GetAuthorizationTokenAsync(Uri uri, Dictionary<string, object>? additionalAuthenticationContext = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(accessToken);
}
