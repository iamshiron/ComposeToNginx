using Shiron.ComposeToNginx.Cli.Commands;
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(c => {
    c.SetApplicationName("compose-to-nginx");
    c.SetApplicationVersion("1.0.0");

    c.AddCommand<EchoCommand>("echo");
});

await app.RunAsync(args);
