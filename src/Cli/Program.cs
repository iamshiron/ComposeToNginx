using DotNetEnv;
using Microsoft.Extensions.DependencyInjection;
using Shiron.ComposeToNginx.Cli.Commands;
using Shiron.ComposeToNginx.Cli.Commands.Certificates;
using Shiron.ComposeToNginx.Cli.Commands.Hosts;
using Shiron.ComposeToNginx.Cli.Infrastructure;
using Shiron.ComposeToNginx.Cli.Services;
using Shiron.ComposeToNginx.Cli.Services.Impl;
using Shiron.ComposeToNginx.Core.Certificates;
using Shiron.ComposeToNginx.Core.Planning;
using Shiron.Lib.DockerUtils;
using Spectre.Console.Cli;

// Load .env into the environment if present (does not override existing env vars).
Env.Load();

var services = new ServiceCollection();
services.AddSingleton<INpmClientFactory, NpmClientFactory>();
services.AddSingleton<IComposeReader, ComposeReader>();
services.AddSingleton<ICertificateResolver, CertificateResolver>();
services.AddSingleton<HostPlanner>();

var registrar = new TypeRegistrar(services);
var app = new CommandApp(registrar);
app.Configure(c => {
    c.SetApplicationName("compose-to-nginx");
    c.SetApplicationVersion("1.0.0");

    c.AddBranch("hosts", b => {
        b.AddCommand<AsyncListHostsCommand>("ls");
        b.AddCommand<AsyncAddHostCommand>("add");
        b.AddCommand<AsyncPushHostsCommand>("push");
    });
    c.AddBranch("certificates", b => {
        b.AddCommand<AsyncListCertificatesCommand>("ls");
    });
});

return await app.RunAsync(args);
