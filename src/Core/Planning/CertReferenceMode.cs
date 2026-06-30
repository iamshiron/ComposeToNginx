namespace Shiron.ComposeToNginx.Core.Planning;

/// <summary>
/// Controls how the <c>npm.cert</c> label is written during a
/// <c>hosts pull</c> run, for services whose matching NGINX Proxy Manager host
/// has a certificate attached.
/// </summary>
public enum CertReferenceMode {
    /// <summary>
    /// Do not write <c>npm.cert</c>. On <c>push</c>, the certificate is inferred
    /// from the host domain via domain-coverage matching. Produces the shortest
    /// labels and is unambiguous when a single certificate covers each domain.
    /// </summary>
    None,
    /// <summary>
    /// Write the certificate's <c>nice_name</c> verbatim. Explicit and survives
    /// domain-coverage ambiguity, but brittle if the certificate's domain order
    /// (and therefore its auto-generated nice-name) ever changes.
    /// </summary>
    NiceName
}
