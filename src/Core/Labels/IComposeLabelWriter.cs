namespace Shiron.ComposeToNginx.Core.Labels;

/// <summary>
/// Writes a set of label edits into a Docker Compose file, preserving every
/// service's existing (non-target) labels <em>and</em> the file's comments and
/// formatting. Used by the <c>hosts pull</c> command to backfill <c>npm.*</c>
/// labels derived from NGINX Proxy Manager onto an existing compose file.
/// </summary>
/// <remarks>
/// Implementations edit only the narrow slice of text that makes up each
/// service's <c>labels:</c> block — they never re-serialise the whole document
/// — and must re-parse the result to verify the edits are correct, refusing to
/// return a corrupted file.
/// </remarks>
public interface IComposeLabelWriter {
    /// <summary>
    /// Returns a copy of <paramref name="composeYaml"/> in which each named
    /// service's <c>labels</c> block contains the supplied label key/value
    /// pairs. Existing non-target labels, comments and formatting outside the
    /// edited <c>labels</c> blocks are preserved verbatim.
    /// </summary>
    /// <param name="composeYaml">The raw Docker Compose YAML.</param>
    /// <param name="labelsByService">
    /// A map of service name to the labels to set. Within each service, keys
    /// present here overwrite existing values with the same key; all other
    /// labels are kept.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when the input YAML is malformed.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a service's labels block uses a shape that cannot be edited
    /// safely (e.g. inline/flow style), or when the post-edit verification fails.
    /// </exception>
    string ApplyLabels(string composeYaml, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> labelsByService);
}
