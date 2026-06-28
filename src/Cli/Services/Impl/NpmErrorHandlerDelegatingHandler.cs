using System.Net.Http;
using System.Text.Json;

namespace Shiron.ComposeToNginx.Cli.Services.Impl;

/// <summary>
/// HTTP pipeline handler that inspects non-success responses and throws an
/// <see cref="NpmApiException"/> carrying the actual error message parsed from
/// the response body. Without this, Kiota surfaces only the status code because
/// the generated SDK registers no error mapping.
/// </summary>
public sealed class NpmErrorHandlerDelegatingHandler : DelegatingHandler {
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return response;

        var statusCode = (int) response.StatusCode;
        var message = await ExtractErrorMessageAsync(response, statusCode, cancellationToken).ConfigureAwait(false);
        response.Dispose();
        throw new NpmApiException(statusCode, message);
    }

    private static async Task<string> ExtractErrorMessageAsync(HttpResponseMessage response, int statusCode, CancellationToken cancellationToken) {
        string raw;
        try {
            raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        } catch {
            return $"The server returned HTTP {statusCode} with an unreadable response body.";
        }

        if (string.IsNullOrWhiteSpace(raw)) {
            return $"The server returned HTTP {statusCode} with no response body.";
        }

        var parsed = TryParseJsonMessage(raw);
        return parsed ?? raw.Trim();
    }

    /// <summary>
    /// Attempts to extract a human-readable message from common NGINX Proxy
    /// Manager error payloads: <c>{ "error": { "message": "..." } }</c>,
    /// <c>{ "errors": [...] }</c>, or a top-level <c>message</c>.
    /// </summary>
    private static string? TryParseJsonMessage(string raw) {
        try {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (root.TryGetProperty("error", out var error) && TryReadMessage(error, out var msg)) return msg;

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array) {
                var messages = new List<string>();
                foreach (var item in errors.EnumerateArray()) {
                    if (TryReadMessage(item, out var m)) messages.Add(m);
                }
                if (messages.Count > 0) return string.Join("; ", messages);
            }

            if (TryReadMessage(root, out var topLevel)) return topLevel;
        } catch (JsonException) {
            // Body is not JSON — caller falls back to the raw text.
        }

        return null;
    }

    private static bool TryReadMessage(JsonElement element, out string message) {
        message = string.Empty;
        if (element.ValueKind == JsonValueKind.String) {
            var value = element.GetString();
            if (!string.IsNullOrWhiteSpace(value)) {
                message = value;
                return true;
            }
            return false;
        }

        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("message", out var messageElement)
            && messageElement.ValueKind == JsonValueKind.String) {
            var value = messageElement.GetString();
            if (!string.IsNullOrWhiteSpace(value)) {
                message = value;
                return true;
            }
        }

        return false;
    }
}
