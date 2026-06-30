namespace Shiron.ComposeToNginx.Core.Npm;

/// <summary>
/// Thrown when NGINX Proxy Manager returns a non-success HTTP status code.
/// Carries the actual error message parsed from the response body.
/// </summary>
public class NpmApiException : Exception {
    /// <summary>The HTTP status code returned by the server.</summary>
    public int StatusCode { get; }

    public NpmApiException(int statusCode, string message) : base(message) => StatusCode = statusCode;
}
