namespace Shiron.ComposeToNginx.Cli;

/// <summary>
/// Fully resolved, validated connection details for NGINX Proxy Manager.
/// </summary>
public sealed record NpmConnectionOptions(string BaseUrl, string Email, string Password);
