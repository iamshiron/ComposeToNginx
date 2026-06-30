namespace Shiron.ComposeToNginx.Core.Npm;

/// <summary>
/// A lightweight, SDK-independent view of an NPM certificate, used for
/// resolution and display without coupling to the generated Kiota models.
/// </summary>
public sealed record NpmCertificateInfo(
    int Id,
    string? NiceName,
    IReadOnlyList<string> DomainNames,
    string? Provider,
    string? ExpiresOn
);
