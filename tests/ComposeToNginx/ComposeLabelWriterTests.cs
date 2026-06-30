using Shiron.ComposeToNginx.Core.Labels;
using Shiron.Lib.DockerUtils;
using Xunit;

namespace Shiron.ComposeToNginx.Tests;

public class ComposeLabelWriterTests {
    private readonly ComposeLabelWriter _writer = new();
    private readonly IComposeReader _reader = new ComposeReader();

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Edits(
        params (string Service, (string Key, string Value)[] Labels)[] services
    ) {
        var dict = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (service, labels) in services)
            dict[service] = labels.ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal);
        return dict;
    }

    private IReadOnlyDictionary<string, string> ReadBack(string yaml, string service) {
        var svc = _reader.Read(yaml).Single(s => s.Name == service);
        return svc.Labels.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    // ── Comment survival + no corruption (the headline guarantee) ───────────

    [Fact]
    public void ApplyLabels_PreservesAllCommentsAndDoesNotCorruptFile() {
        var yaml = """
# Top-level comment
services:
  api: # inline comment on service
    image: nginx:latest  # inline comment on image
    ports:
      - "8080:80"
    # comment before labels
    labels:
      # comment inside labels block
      app.kubernetes.io/name: api  # keep me

  # comment between services
  web:
    image: web:latest
    ports:
      - "9090:90"
""";

        var edits = Edits(("api", [("npm.host", "api.example.com"), ("npm.ssl", "true")]));
        var result = _writer.ApplyLabels(yaml, edits);

        // Every comment survived, verbatim.
        Assert.Contains("# Top-level comment", result);
        Assert.Contains("# inline comment on service", result);
        Assert.Contains("# inline comment on image", result);
        Assert.Contains("# comment before labels", result);
        Assert.Contains("# comment inside labels block", result);
        Assert.Contains("# keep me", result);
        Assert.Contains("# comment between services", result);

        // The file is not corrupt: it re-parses and yields the right structure.
        var labels = ReadBack(result, "api");
        Assert.Equal("api.example.com", labels["npm.host"]);
        Assert.Equal("true", labels["npm.ssl"]);
        Assert.Equal("api", labels["app.kubernetes.io/name"]);

        // The untouched service is intact and still has no labels.
        Assert.Empty(ReadBack(result, "web"));
    }

    // ── Block creation / merge / replacement ────────────────────────────────

    [Fact]
    public void ApplyLabels_ServiceWithoutLabels_CreatesLabelsBlock() {
        var yaml = """
services:
  api:
    image: nginx:latest
    ports:
      - "8080:80"
""";

        var result = _writer.ApplyLabels(yaml, Edits(("api", [("npm.host", "api.example.com")])));

        Assert.Equal("api.example.com", ReadBack(result, "api")["npm.host"]);
        // Existing keys remain intact and parseable.
        Assert.Single(_reader.Read(result).Single(s => s.Name == "api").Ports);
    }

    [Fact]
    public void ApplyLabels_ExistingNonNpmLabels_ArePreservedAlongsideNewOnes() {
        var yaml = """
services:
  api:
    image: x
    labels:
      traefik.enable: "true"
      org.team: backend
""";

        var labels = ReadBack(
            _writer.ApplyLabels(yaml, Edits(("api", [("npm.host", "api.example.com")]))),
            "api"
        );

        Assert.Equal("api.example.com", labels["npm.host"]);
        Assert.Equal("true", labels["traefik.enable"]);
        Assert.Equal("backend", labels["org.team"]);
    }

    [Fact]
    public void ApplyLabels_StaleNpmLabels_AreReplacedNotDuplicated() {
        var yaml = """
services:
  api:
    image: x
    labels:
      npm.host: old.example.com
      npm.ssl: "false"
      app: kept
""";

        var result = _writer.ApplyLabels(yaml, Edits(("api", [("npm.host", "new.example.com"), ("npm.ssl", "true")])));
        var labels = ReadBack(result, "api");

        Assert.Equal("new.example.com", labels["npm.host"]);
        Assert.Equal("true", labels["npm.ssl"]);
        Assert.False(labels.ContainsKey("npm.force-ssl"));
        Assert.Equal("kept", labels["app"]);
        Assert.Equal(1, result.Split('\n').Count(l => l.TrimStart().StartsWith("npm.host:", StringComparison.Ordinal)));
    }

    [Fact]
    public void ApplyLabels_SequenceFormLabels_HandledInPlace() {
        var yaml = """
services:
  api:
    image: x
    labels:
      - app.foo=bar
      - npm.host=old.example.com
""";

        var labels = ReadBack(
            _writer.ApplyLabels(yaml, Edits(("api", [("npm.host", "new.example.com")]))),
            "api"
        );

        Assert.Equal("new.example.com", labels["npm.host"]);
        Assert.Equal("bar", labels["app.foo"]); // sequence sibling preserved
    }

    [Fact]
    public void ApplyLabels_MultipleServices_AllEdited() {
        var yaml = """
services:
  api:
    image: x
  web:
    image: y
""";

        var result = _writer.ApplyLabels(yaml, Edits(
            ("api", [("npm.host", "api.example.com")]),
            ("web", [("npm.host", "web.example.com")])
        ));

        Assert.Equal("api.example.com", ReadBack(result, "api")["npm.host"]);
        Assert.Equal("web.example.com", ReadBack(result, "web")["npm.host"]);
    }

    // ── Formatting preservation ────────────────────────────────────────────

    [Fact]
    public void ApplyLabels_PreservesQuotesAndBlankLinesOutsideEditedBlock() {
        var yaml = """
services:
  api:
    image: "nginx:latest"
    environment:
      KEY: "value"

  web:
    image: y
    ports:
      - "9090:90"
""";

        var result = _writer.ApplyLabels(yaml, Edits(("web", [("npm.host", "web.example.com")])));

        Assert.Contains("image: \"nginx:latest\"", result);
        Assert.Contains("KEY: \"value\"", result);
        Assert.Contains("\"9090:90\"", result);
        Assert.Contains("      KEY: \"value\"\n\n  web:", result);
    }

    [Fact]
    public void ApplyLabels_PreservesCrlfLineEndings() {
        var yaml = "services:\r\n  api:\r\n    image: x\r\n";

        var result = _writer.ApplyLabels(yaml, Edits(("api", [("npm.host", "api.example.com")])));

        Assert.Contains("\r\n", result);
        Assert.Equal("api.example.com", ReadBack(result, "api")["npm.host"]);
    }

    // ── Safety: unsupported shapes refuse rather than corrupt ──────────────

    [Fact]
    public void ApplyLabels_FlowStyleLabels_ThrowsAndDoesNotReturnCorruptOutput() {
        var yaml = """
services:
  api:
    image: x
    labels: {app: foo}
""";

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _writer.ApplyLabels(yaml, Edits(("api", [("npm.host", "api.example.com")]))));

        Assert.Contains("inline/flow", ex.Message);
    }

    [Fact]
    public void ApplyLabels_NoEdits_ReturnsInputUnchanged() {
        var yaml = "services:\n  api:\n    image: x\n";
        Assert.Equal(yaml, _writer.ApplyLabels(yaml, new Dictionary<string, IReadOnlyDictionary<string, string>>()));
    }
}
