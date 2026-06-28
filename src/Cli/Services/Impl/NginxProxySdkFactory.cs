using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using NginxProxy.Sdk;

namespace Shiron.ComposeToNginx.Cli.Services.Impl;

/// <summary>
/// Authenticates with NGINX Proxy Manager using email/password credentials and
/// returns an authorized <see cref="NginxProxySdk"/>.
/// </summary>
public sealed class NginxProxySdkFactory : INginxProxySdkFactory {
    public async Task<NginxProxySdk> CreateAsync(NpmConnectionOptions options, CancellationToken cancellationToken = default) {
        // A single HttpClient (with the error handler) is shared by the
        // unauthenticated token request and the authenticated data requests.
        var httpClient = CreateHttpClient();

        var token = await RequestTokenAsync(options, httpClient, cancellationToken).ConfigureAwait(false);

        var authProvider = new BaseBearerTokenAuthenticationProvider(new StaticAccessTokenProvider(token));
        var adapter = new HttpClientRequestAdapter(authProvider, parseNodeFactory: null, serializationWriterFactory: null, httpClient, observabilityOptions: null) {
            BaseUrl = options.BaseUrl,
        };
        return new NginxProxySdk(adapter);
    }

    private static HttpClient CreateHttpClient() {
        var handler = new NpmErrorHandlerDelegatingHandler {
            InnerHandler = new HttpClientHandler(),
        };
        return new HttpClient(handler, disposeHandler: true);
    }

    /// <summary>
    /// POSTs credentials to <c>/tokens</c> and returns the JWT. This is done
    /// with a raw request because the generated <c>TokensPostResponse</c> is a
    /// composed type without a discriminator, which Kiota cannot deserialize
    /// reliably (see the <c>/tokens</c> warning in <c>.kiota.log</c>).
    /// </summary>
    private static async Task<string> RequestTokenAsync(NpmConnectionOptions options, HttpClient httpClient, CancellationToken cancellationToken) {
        var payload = JsonSerializer.Serialize(new { identity = options.Email, secret = options.Password });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/tokens") {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        // Non-success responses are surfaced as NpmApiException by the delegating handler.
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("token", out var tokenElement)
            && tokenElement.ValueKind == JsonValueKind.String
            && tokenElement.GetString() is { Length: > 0 } token) {
            return token;
        }

        if (doc.RootElement.TryGetProperty("requires_2fa", out var twoFa) && twoFa.ValueKind == JsonValueKind.True) {
            throw new InvalidOperationException("Authentication failed: two-factor authentication is required for this account.");
        }

        throw new InvalidOperationException("Authentication failed: NGINX Proxy Manager did not return an access token.");
    }
}
