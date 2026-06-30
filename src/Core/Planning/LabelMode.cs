namespace Shiron.ComposeToNginx.Core.Planning;

/// <summary>
/// Controls how Docker Compose <c>npm.*</c> labels are interpreted.
/// </summary>
public enum LabelMode {
    /// <summary>Use labels when present; fall back to interactive prompts otherwise.</summary>
    Auto,
    /// <summary>Every ported service must have an <c>npm.host</c> label; error if missing.</summary>
    Require,
    /// <summary>Ignore labels entirely; always use interactive prompts.</summary>
    Ignore
}
