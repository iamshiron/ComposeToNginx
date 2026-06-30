namespace Shiron.ComposeToNginx.Core.Planning;

/// <summary>
/// A planned set of <c>npm.*</c> labels to write onto a Compose service,
/// derived from a matching existing NGINX Proxy Manager host. Produced by
/// <see cref="PullPlanner"/>; the CLI renders and applies it.
/// </summary>
/// <param name="Service">The Compose service name the labels belong to.</param>
/// <param name="MatchedHostId">The id of the NPM proxy host this edit was derived from.</param>
/// <param name="MatchedDomains">A display string of the matched host's domain names.</param>
/// <param name="ForwardTarget">A display string of the matched host's forward target (<c>scheme://host:port</c>).</param>
/// <param name="Labels">The <c>npm.*</c> label key/value pairs to write. Only keys that deviate from the parser's defaults are included, so the result round-trips through <see cref="Labels.LabelConfigParser"/> with no lossy change.</param>
public sealed record PlannedLabelEdit(
    string Service,
    int MatchedHostId,
    string MatchedDomains,
    string ForwardTarget,
    IReadOnlyDictionary<string, string> Labels
);
