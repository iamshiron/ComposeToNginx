namespace Shiron.ComposeToNginx.Core.Labels;

/// <summary>
/// Thrown when a Docker Compose <c>npm.*</c> label contains an invalid or
/// unresolvable value.
/// </summary>
public class LabelConfigException : Exception {
    /// <summary>The name of the service whose label is malformed.</summary>
    public string ServiceName { get; }

    /// <summary>The label key that caused the error (e.g. <c>npm.host</c>).</summary>
    public string Label { get; }

    public LabelConfigException(string serviceName, string label, string message)
        : base($"Service '{serviceName}': label '{label}' — {message}") {
        ServiceName = serviceName;
        Label = label;
    }
}
