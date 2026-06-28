using Spectre.Console.Cli;

namespace Shiron.ComposeToNginx.Cli.Commands;

internal class AsyncPushCommand : AsyncCommand {
    protected override Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken) {
        throw new NotImplementedException();
    }
}
