using Spectre.Console;
using Spectre.Console.Cli;

namespace Shiron.ComposeToNginx.Cli.Commands;

internal class EchoCommand : Command {
    protected override int Execute(CommandContext context, CancellationToken cancellationToken) {
        AnsiConsole.WriteLine("Echo!");
        return 0;
    }
}
