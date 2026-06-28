using Spectre.Console.Cli;
using System.ComponentModel;

namespace Shiron.ComposeToNginx.Cli.Commands;

/// <summary>
/// Shared connection settings for any command that talks to NGINX Proxy Manager.
/// Values are resolved with the following precedence:
/// command-line flag, then environment variable (or <c>.env</c>), then the default.
/// </summary>
public abstract class NpmConnectionSettings : CommandSettings {
    private const string DefaultHost = "http://127.0.0.1:81";

    private const string HostEnvKey = "NPM_HOST";
    private const string EmailEnvKey = "NPM_EMAIL";
    private const string PasswordEnvKey = "NPM_PASSWORD";

    [CommandOption("--host")]
    [Description("The host of NGINX Proxy Manager, e.g. http://127.0.0.1:81")]
    public string? Host { get; init; }

    [CommandOption("--email")]
    [Description("The email used to authenticate with NGINX Proxy Manager.")]
    public string? Email { get; init; }

    [CommandOption("--password")]
    [Description("The password used to authenticate with NGINX Proxy Manager.")]
    public string? Password { get; init; }

    /// <summary>
    /// Resolves the raw flags into a fully validated <see cref="NpmConnectionOptions"/>,
    /// applying environment variable fallbacks and building the API base URL.
    /// </summary>
    public NpmConnectionOptions ToConnectionOptions() {
        var host = Resolve(Host, HostEnvKey, DefaultHost)!;
        var email = Resolve(Email, EmailEnvKey, required: true, optionName: nameof(Email))!;
        var password = Resolve(Password, PasswordEnvKey, required: true, optionName: nameof(Password))!;

        var baseUrl = host.TrimEnd('/') + "/api";
        return new NpmConnectionOptions(baseUrl, email, password);
    }

    private static string? Resolve(string? flagValue, string envKey, string? defaultValue = null, bool required = false, string? optionName = null) {
        var value = !string.IsNullOrWhiteSpace(flagValue)
            ? flagValue
            : Environment.GetEnvironmentVariable(envKey) is { Length: > 0 } env
                ? env
                : defaultValue;

        if (required && string.IsNullOrWhiteSpace(value)) {
            var hint = optionName is null ? string.Empty : $" (--{optionName.ToLowerInvariant()} flag or {envKey})";
            throw new InvalidOperationException($"A required value was not supplied{hint}.");
        }

        return value;
    }
}
