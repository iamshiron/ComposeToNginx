using Shiron.Lib.DockerUtils.Model;

namespace Shiron.ComposeToNginx.Tests;

internal static class TestServiceFactory {
    public static Service MakeService(
        string name = "api",
        string? containerName = null,
        string[]? ports = null,
        Dictionary<string, string>? labels = null
    ) {
        var portForwards = (ports ?? []).Select(p => {
            var parts = p.Split(':');
            return new PortForward {
                ContainerPort = parts.Length > 1 ? parts[1] : parts[0],
                HostPort = parts[0],
            };
        }).ToArray();

        return new Service {
            Name = name,
            Image = "test:latest",
            ContainerName = containerName,
            Restart = null,
            Ports = portForwards,
            Volumes = [],
            Environment = [],
            Networks = [],
            Labels = labels ?? [],
        };
    }
}
