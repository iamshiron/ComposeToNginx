using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace Shiron.ComposeToNginx.Cli.Infrastructure;

/// <summary>
/// Bridges <see cref="IServiceCollection"/> to Spectre.Console.Cli's
/// <see cref="ITypeRegistrar"/>, enabling constructor dependency injection
/// for commands.
/// </summary>
public sealed class TypeRegistrar(IServiceCollection services) : ITypeRegistrar {
    public ITypeResolver Build() => new TypeResolver(services.BuildServiceProvider());

    public void Register(Type service, Type implementation) => services.AddSingleton(service, implementation);

    public void RegisterInstance(Type service, object implementation) => services.AddSingleton(service, implementation);

    public void RegisterLazy(Type service, Func<object> factory) => services.AddSingleton(service, _ => factory());
}

/// <summary>
/// Resolves services from a built <see cref="IServiceProvider"/>.
/// </summary>
public sealed class TypeResolver(IServiceProvider provider) : ITypeResolver {
    public object? Resolve(Type? type) => type is null ? null : provider.GetService(type);
}
