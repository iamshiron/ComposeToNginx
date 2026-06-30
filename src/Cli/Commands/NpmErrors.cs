using Shiron.ComposeToNginx.Core.Npm;
using Spectre.Console;

namespace Shiron.ComposeToNginx.Cli.Commands;

/// <summary>
/// Shared error reporting for commands. Writes a styled error line describing
/// an exception and returns the standard failure exit code.
/// </summary>
public static class NpmErrors {
    /// <summary>
    /// Writes <paramref name="prefix"/>: <paramref name="ex"/> to the console,
    /// surfacing the HTTP status code and server message for
    /// <see cref="NpmApiException"/>. Returns <c>1</c>.
    /// </summary>
    public static int WriteError(this IAnsiConsole console, string prefix, Exception ex) {
        var detail = ex is NpmApiException api ? $"({api.StatusCode}) {api.Message}" : ex.Message;
        console.MarkupLine($"[red]{Markup.Escape(prefix)}:[/] {Markup.Escape(detail)}");
        return 1;
    }
}
