using Spectre.Console.Cli;
using System.ComponentModel;

namespace Shiron.ComposeToNginx.Cli.Commands;

/// <summary>
/// Shared settings for commands that <strong>mutate</strong> NGINX Proxy Manager
/// state. Adds transactional (<see cref="DryRun"/>) and non-interactive
/// (<see cref="Yes"/>) flags on top of the connection settings.
/// </summary>
/// <remarks>
/// <para>
/// <b>Transactional model.</b> Every mutating command follows the same flow:
/// </para>
/// <list type="number">
///   <item><b>Accumulate</b> — gather every change that needs to be applied (resolve certificates, detect conflicts, build request bodies). If anything fails, the command <b>aborts</b> without touching NPM.</item>
///   <item><b>Present</b> — render a table or summary of the planned changes.</item>
///   <item><b>Confirm</b> — ask for explicit approval (skipped when <see cref="Yes"/> or <see cref="DryRun"/> is set).</item>
///   <item><b>Apply</b> — execute the changes against NPM (skipped under <see cref="DryRun"/>).</item>
/// </list>
/// </remarks>
public abstract class NpmMutationSettings : NpmConnectionSettings {
    [CommandOption("--dry-run")]
    [Description("Accumulate and preview the planned changes without applying them.")]
    public bool DryRun { get; init; }

    [CommandOption("--yes|-y")]
    [Description("Skip the confirmation prompt before applying changes. Auto-enabled when the CI environment variable is set and stdout is not a TTY.")]
    public bool Yes { get; init; }

    /// <summary>
    /// <c>true</c> when the command must never prompt the user — either because
    /// <see cref="Yes"/> was explicitly passed, or because a CI environment was
    /// detected with non-interactive (redirected) output.
    /// </summary>
    public bool IsNonInteractive =>
        Yes || (Environment.GetEnvironmentVariable("CI") is not null && Console.IsOutputRedirected);
}
