using System.Diagnostics.CodeAnalysis;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Shiron.ComposeToNginx.Core.Labels;

/// <summary>
/// <see cref="IComposeLabelWriter"/> that preserves comments and formatting by
/// editing only the raw text of each service's <c>labels:</c> block, never
/// re-serialising the whole document.
/// </summary>
/// <remarks>
/// <para>
/// <b>Approach.</b> The file is parsed with YamlDotNet purely to locate each
/// target service (line + indent) and to read its existing labels. The edits
/// themselves are performed on the raw line array: stale <c>npm.*</c> entries
/// are removed from a service's labels block and the new ones inserted right
/// after the <c>labels:</c> header; if a service has no labels block, one is
/// spliced in at the end of its body. Every other line — comments, quotes,
/// blank lines, ordering — is left untouched.
/// </para>
/// <para>
/// <b>Safety.</b> After editing, the result is re-parsed and each target
/// service is checked: the intended labels must be present, no stale
/// <c>npm.*</c> labels may remain, and every non-target label must be
/// preserved. If any check fails (including unsupported shapes like flow-style
/// labels), an <see cref="InvalidOperationException"/> is thrown rather than
/// returning a corrupted file.
/// </para>
/// </remarks>
public sealed class ComposeLabelWriter : IComposeLabelWriter {
    private const string ServicesKey = "services";
    private const string LabelsKey = "labels";
    private const int DefaultIndentStep = 2;

    /// <inheritdoc/>
    public string ApplyLabels(string composeYaml, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> labelsByService) {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeYaml);
        ArgumentNullException.ThrowIfNull(labelsByService);
        if (labelsByService.Count == 0) return composeYaml;

        var newline = composeYaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = new List<string>(composeYaml.Replace("\r\n", "\n").Split('\n'));

        var stream = Parse(composeYaml);
        if (!TryGetServicesMapping(stream, out var services))
            throw new ArgumentException("No top-level 'services' mapping found in the compose file.");

        // Collect targets (name, service-key line, indent) and capture each
        // service's original non-npm labels for later verification.
        var targets = new List<Target>();
        var originalNonNpm = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (keyNode, valueNode) in services.Children) {
            if (keyNode is not YamlScalarNode keyScalar || keyScalar.Value is null) continue;
            if (!labelsByService.ContainsKey(keyScalar.Value)) continue;
            if (keyScalar.Start.Line < 1)
                throw new InvalidOperationException($"Service '{keyScalar.Value}': could not locate its line in the file.");
            if (valueNode is not YamlMappingNode serviceMapping)
                throw new InvalidOperationException($"Service '{keyScalar.Value}': expected a mapping and cannot be edited.");

            var full = ReadLabels(serviceMapping);
            originalNonNpm[keyScalar.Value] = full
                .Where(kv => !kv.Key.StartsWith("npm.", StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

            targets.Add(new Target(keyScalar.Value, ToLineIndex(keyScalar.Start.Line), ToIndent(keyScalar.Start.Column)));
        }

        if (targets.Count == 0) return composeYaml;

        // Apply edits bottom-up so earlier line indices stay valid.
        foreach (var target in targets.OrderByDescending(t => t.ServiceKeyLine)) {
            var intended = labelsByService[target.Name];
            ApplyToService(lines, target, intended);
        }

        var result = string.Join(newline, lines);
        if (!result.EndsWith(newline, StringComparison.Ordinal)) result += newline;

        Verify(result, labelsByService, originalNonNpm);
        return result;
    }

    // ── Per-service editing ─────────────────────────────────────────────────

    private static void ApplyToService(List<string> lines, Target target, IReadOnlyDictionary<string, string> intended) {
        var serviceBlock = ScanBlock(lines, target.ServiceKeyLine, target.ServiceIndent);
        var serviceStep = serviceBlock is { } sb && sb.FirstItemIndent > target.ServiceIndent
            ? sb.FirstItemIndent - target.ServiceIndent
            : DefaultIndentStep;
        var childIndent = serviceBlock?.FirstItemIndent ?? target.ServiceIndent + serviceStep;
        var bodyEnd = serviceBlock?.EndExclusive ?? target.ServiceKeyLine + 1;

        var labelsLine = FindLabelsLine(lines, target.ServiceKeyLine, bodyEnd, childIndent, out var inlineFlow);

        // No labels block — splice a new one in at the end of the service body.
        if (labelsLine < 0) {
            var newItemIndent = childIndent + serviceStep;
            var insert = new List<string> { $"{Spaces(childIndent)}{LabelsKey}:" };
            insert.AddRange(FormatMappingItems(newItemIndent, intended));
            var insertAt = LastContentLine(lines, target.ServiceKeyLine + 1, bodyEnd) + 1;
            lines.InsertRange(insertAt, insert);
            return;
        }

        if (inlineFlow)
            throw new InvalidOperationException($"Service '{target.Name}': its labels block uses inline/flow style and cannot be edited without rewriting the file. Convert it to block style first.");

        var labelsIndent = IndentOf(lines[labelsLine]);
        var labelsBlock = ScanBlock(lines, labelsLine, labelsIndent);
        var isSequence = labelsBlock is { } lb && lines[lb.FirstItemLine].TrimStart().StartsWith('-');
        var itemIndent = labelsBlock?.FirstItemIndent ?? labelsIndent + serviceStep;
        var blockEnd = labelsBlock?.EndExclusive ?? labelsLine + 1;

        // Remove stale npm.* entries (high → low to keep indices valid).
        for (var i = blockEnd - 1; i > labelsLine; i--) {
            var isNpm = isSequence ? IsNpmSequenceItem(lines[i]) : IsNpmMappingItem(lines[i]);
            if (isNpm) lines.RemoveAt(i);
        }

        // Insert the new npm.* entries directly under the labels header.
        var items = isSequence
            ? FormatSequenceItems(itemIndent, intended)
            : FormatMappingItems(itemIndent, intended);
        lines.InsertRange(labelsLine + 1, items);
    }

    // ── Block scanning ──────────────────────────────────────────────────────

    private sealed record BlockSpan(int FirstItemLine, int FirstItemIndent, int EndExclusive);

    /// <summary>
    /// Scans the block nested under <paramref name="headerLine"/>: the run of
    /// consecutive lines whose indentation is greater than
    /// <paramref name="headerIndent"/> (blank/comment lines are skipped and do
    /// not terminate the block).
    /// </summary>
    private static BlockSpan? ScanBlock(List<string> lines, int headerLine, int headerIndent) {
        var i = headerLine + 1;
        var firstItemLine = -1;
        var firstItemIndent = -1;

        while (i < lines.Count) {
            var line = lines[i];
            if (IsBlankOrComment(line)) { i++; continue; }
            var ind = IndentOf(line);
            if (ind <= headerIndent) break;
            if (firstItemLine < 0) { firstItemLine = i; firstItemIndent = ind; }
            i++;
        }

        return firstItemLine < 0 ? null : new BlockSpan(firstItemLine, firstItemIndent, i);
    }

    /// <summary>
    /// Finds the line index of the direct-child <c>labels:</c> key within a
    /// service body. Returns -1 when absent. <paramref name="inlineFlow"/> is
    /// set when the labels value is given on the same line (flow/scalar form).
    /// </summary>
    private static int FindLabelsLine(List<string> lines, int serviceKeyLine, int bodyEnd, int childIndent, out bool inlineFlow) {
        inlineFlow = false;
        for (var i = serviceKeyLine + 1; i < bodyEnd; i++) {
            var line = lines[i];
            if (IsBlankOrComment(line)) continue;
            if (IndentOf(line) != childIndent) continue;

            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith($"{LabelsKey}:", StringComparison.Ordinal)) continue;

            var after = trimmed.Substring($"{LabelsKey}:".Length).TrimStart();
            if (after.Length == 0 || after.StartsWith('#')) return i;
            inlineFlow = true;
            return i;
        }
        return -1;
    }

    private static int LastContentLine(List<string> lines, int from, int until) {
        var last = from - 1;
        for (var i = from; i < until; i++)
            if (!IsBlankOrComment(lines[i])) last = i;
        return last;
    }

    // ── Formatting & detection ──────────────────────────────────────────────

    private static List<string> FormatMappingItems(int indent, IEnumerable<KeyValuePair<string, string>> labels) {
        var prefix = Spaces(indent);
        return labels.Select(kv => $"{prefix}{kv.Key}: {QuoteIfNeeded(kv.Value)}").ToList();
    }

    private static List<string> FormatSequenceItems(int indent, IEnumerable<KeyValuePair<string, string>> labels) {
        var prefix = Spaces(indent);
        return labels.Select(kv => $"{prefix}- {kv.Key}={kv.Value}").ToList();
    }

    private static bool IsNpmMappingItem(string line) {
        var t = line.TrimStart();
        return t.StartsWith("npm.", StringComparison.Ordinal) && t.Contains(':');
    }

    private static bool IsNpmSequenceItem(string line) {
        var t = line.TrimStart();
        if (!t.StartsWith('-')) return false;
        var item = t[1..].TrimStart().Trim('"');
        var key = item.IndexOf('=') is { } eq and > 0 ? item[..eq] : item;
        return key.StartsWith("npm.", StringComparison.Ordinal);
    }

    /// <summary>
    /// Quotes a scalar value only when YAML would otherwise misinterpret it
    /// (empty, leading special character, embedded "<c>: </c>" or
    /// "<c> #</c>", or surrounding spaces). Bool-like values such as
    /// <c>true</c>/<c>false</c> are intentionally left unquoted.
    /// </summary>
    private static string QuoteIfNeeded(string value) {
        if (value.Length == 0) return "\"\"";
        if (value.StartsWith(' ') || value.EndsWith(' ')
            || value.Contains(": ") || value.Contains(" #")
            || "&*!|>%@\"'`,[]{}".IndexOf(value[0]) >= 0) {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
        return value;
    }

    private static int IndentOf(string line) {
        var i = 0;
        while (i < line.Length) {
            var c = line[i];
            if (c == ' ') i++;
            else if (c == '\t') throw new InvalidOperationException("Tab indentation is not supported; convert the file to spaces first.");
            else break;
        }
        return i;
    }

    private static bool IsBlankOrComment(string line) {
        var trimmed = line.AsSpan().Trim();
        return trimmed.IsEmpty || trimmed[0] == '#';
    }

    private static string Spaces(int count) => new(' ', Math.Max(0, count));

    /// <summary>
    /// Converts a 1-based YamlDotNet line number to a 0-based line index,
    /// clamped to be non-negative.
    /// </summary>
    private static int ToLineIndex(long line) => (int) Math.Max(0, line - 1);

    /// <summary>
    /// Converts a 1-based YamlDotNet column number to a 0-based indentation
    /// width, clamped to be non-negative.
    /// </summary>
    private static int ToIndent(long column) => (int) Math.Max(0, column - 1);

    // ── Parse + read labels ─────────────────────────────────────────────────

    private static YamlStream Parse(string yaml) {
        var stream = new YamlStream();
        try {
            stream.Load(new StringReader(yaml));
        } catch (YamlException ex) {
            throw new ArgumentException($"Invalid Docker Compose YAML: {ex.Message}", ex);
        }
        return stream;
    }

    private static bool TryGetServicesMapping(YamlStream stream, [NotNullWhen(true)] out YamlMappingNode? services) {
        foreach (var document in stream.Documents) {
            if (document.RootNode is YamlMappingNode root && TryGetMappingChild(root, ServicesKey, out services))
                return true;
        }
        services = null;
        return false;
    }

    private static Dictionary<string, string> ReadLabels(YamlMappingNode serviceMapping) {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!TryGetChild(serviceMapping, LabelsKey, out var node)) return result;

        switch (node) {
            case YamlMappingNode mapping:
                foreach (var (k, v) in mapping.Children) {
                    if (k is YamlScalarNode kk && kk.Value is not null)
                        result[kk.Value] = v is YamlScalarNode vv ? vv.Value ?? "" : "";
                }
                break;
            case YamlSequenceNode sequence:
                foreach (var item in sequence.Children) {
                    if (item is not YamlScalarNode ss || ss.Value is null) continue;
                    var eq = ss.Value.IndexOf('=');
                    if (eq > 0) result[ss.Value[..eq]] = ss.Value[(eq + 1)..];
                    else result[ss.Value] = "";
                }
                break;
        }
        return result;
    }

    // ── Verification ────────────────────────────────────────────────────────

    private static void Verify(
        string resultYaml,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> intended,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> originalNonNpm
    ) {
        YamlStream parsed;
        try {
            parsed = Parse(resultYaml);
        } catch (ArgumentException ex) {
            throw new InvalidOperationException("Verification failed: the edited file is not valid YAML.", ex);
        }

        if (!TryGetServicesMapping(parsed, out var services))
            throw new InvalidOperationException("Verification failed: the edited file no longer has a 'services' mapping.");

        var byName = new Dictionary<string, YamlMappingNode>(StringComparer.Ordinal);
        foreach (var (k, v) in services.Children)
            if (k is YamlScalarNode ks && ks.Value is not null && v is YamlMappingNode sm)
                byName[ks.Value] = sm;

        foreach (var (name, intent) in intended) {
            if (!byName.TryGetValue(name, out var svc))
                throw new InvalidOperationException($"Verification failed: service '{name}' is missing from the edited file.");

            var got = ReadLabels(svc);

            foreach (var (key, value) in intent)
                if (!got.TryGetValue(key, out var gotValue) || gotValue != value)
                    throw new InvalidOperationException($"Verification failed: label '{key}' on service '{name}' was not written correctly.");

            foreach (var (key, _) in got)
                if (key.StartsWith("npm.", StringComparison.Ordinal) && !intent.ContainsKey(key))
                    throw new InvalidOperationException($"Verification failed: stale label '{key}' remains on service '{name}'.");

            foreach (var (key, value) in originalNonNpm[name])
                if (!got.TryGetValue(key, out var gotValue) || gotValue != value)
                    throw new InvalidOperationException($"Verification failed: existing label '{key}' on service '{name}' was not preserved.");
        }
    }

    // ── YamlMappingNode lookup helpers (the library's are internal) ──────────

    private static bool TryGetMappingChild(YamlMappingNode mapping, string key, [NotNullWhen(true)] out YamlMappingNode? child) {
        if (TryGetChild(mapping, key, out var node) && node is YamlMappingNode m) {
            child = m;
            return true;
        }
        child = null;
        return false;
    }

    private static bool TryGetChild(YamlMappingNode mapping, string key, [NotNullWhen(true)] out YamlNode? child) {
        foreach (var (k, v) in mapping.Children) {
            if (k is YamlScalarNode s && s.Value == key) {
                child = v;
                return true;
            }
        }
        child = null;
        return false;
    }

    private sealed record Target(string Name, int ServiceKeyLine, int ServiceIndent);
}
