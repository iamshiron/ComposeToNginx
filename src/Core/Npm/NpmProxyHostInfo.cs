namespace Shiron.ComposeToNginx.Core.Npm;

/// <summary>
/// A lightweight, SDK-independent view of an existing NPM proxy host, used for
/// overwrite detection and display without coupling to the generated Kiota models.
/// </summary>
public sealed record NpmProxyHostInfo(
    int? Id,
    IReadOnlyList<string> DomainNames,
    string? ForwardHost,
    int? ForwardPort,
    string? ForwardScheme,
    int? CertificateId,
    bool? SslForced,
    bool? Enabled
);
