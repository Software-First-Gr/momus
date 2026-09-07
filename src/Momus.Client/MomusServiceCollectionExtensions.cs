using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Momus.Client.Internal;

namespace Momus.Client;

/// <summary>The whole public surface: one call, no middleware to add, no interceptor to register.</summary>
public static class MomusServiceCollectionExtensions
{
    /// <summary>
    /// Turns on Momus for this application. Call it <b>after</b> your <c>AddDbContext</c> calls:
    /// the way a library reaches every context without asking you to change your options is by
    /// rewriting the <c>DbContextOptions&lt;T&gt;</c> registrations, and it can only rewrite the
    /// ones that already exist. If it finds none, it says so loudly at startup rather than
    /// reporting nothing and leaving you to wonder why.
    /// </summary>
    /// <param name="builder">The host application builder, for its configuration and environment.</param>
    /// <param name="configure">Optional overrides, applied after the <c>Momus:</c> configuration section.</param>
    /// <example>
    /// <code>
    /// builder.Services.AddDbContext&lt;ShopDb&gt;(o => o.UseNpgsql(connectionString));
    /// builder.AddMomus();
    /// </code>
    /// </example>
    public static IHostApplicationBuilder AddMomus(
        this IHostApplicationBuilder builder, Action<MomusOptions>? configure = null)
    {
        Register(builder.Services, builder.Configuration, builder.Environment, configure);
        return builder;
    }

    /// <summary>
    /// The same, for code that only has the service collection. Prefer the
    /// <see cref="AddMomus(IHostApplicationBuilder, Action{MomusOptions})"/> overload: a bare
    /// collection may not yet be able to answer what the configuration or the environment is, and
    /// Momus stays off when it cannot tell.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="configuration">Where to read the <c>Momus:</c> section from, if available.</param>
    /// <param name="configure">Optional overrides, applied after the configuration section.</param>
    public static IServiceCollection AddMomus(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<MomusOptions>? configure = null)
    {
        Register(services, configuration, HostEnvironment(services), configure);
        return services;
    }

    private static void Register(
        IServiceCollection services,
        IConfiguration? configuration,
        IHostEnvironment? environment,
        Action<MomusOptions>? configure)
    {
        var options = new MomusOptions();
        configuration?.GetSection(MomusOptions.Section).Bind(options);
        configure?.Invoke(options);

        if (!IsEnabled(options, environment)) return;

        var queue = new OperationQueue(options);
        var targets = new TargetRegistry(options);
        MomusRuntime.Use(queue);

        services.TryAddSingleton(options);
        services.TryAddSingleton(queue);
        services.TryAddSingleton(targets);

        services.AddHttpClient(MomusExporter.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/");
            // The exporter is a background service; a slow server can only ever delay itself.
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddHostedService<MomusExporter>();
        services.AddSingleton<IStartupFilter, MomusStartupFilter>();

        // Three interceptors, because EF's base classes are one per concern: statements,
        // transactions, connections. They share the operation scope and nothing else.
        var hooked = HookDbContexts(services,
        [
            new MomusCommandInterceptor(options, targets, queue),
            new MomusTransactionInterceptor(options),
            new MomusConnectionInterceptor(options),
        ]);
        services.AddSingleton<IHostedService>(provider => new StartupReport(
            hooked, options, provider.GetRequiredService<ILoggerFactory>().CreateLogger("Momus")));
    }

    /// <summary>
    /// Rewrites every <c>DbContextOptions&lt;T&gt;</c> registration so the options it hands out
    /// carry one more interceptor.
    /// </summary>
    /// <remarks>
    /// Measured against EF Core 8, 9 and 10, with <c>AddDbContext</c>, <c>AddDbContextPool</c> and
    /// <c>AddDbContextFactory</c>. The documented-looking alternative — registering an
    /// <c>IInterceptor</c> in the application's container — does not work on any of them.
    /// </remarks>
    private static int HookDbContexts(IServiceCollection services, IInterceptor[] interceptors)
    {
        var hooked = 0;

        for (var i = 0; i < services.Count; i++)
        {
            var descriptor = services[i];
            if (!descriptor.ServiceType.IsGenericType) continue;
            if (descriptor.ServiceType.GetGenericTypeDefinition() != typeof(DbContextOptions<>)) continue;

            var inner = descriptor.ImplementationFactory;
            if (inner is null) continue;

            services[i] = new ServiceDescriptor(descriptor.ServiceType, provider =>
            {
                var original = (DbContextOptions)inner(provider)!;

                // The builder hands back the same closed generic type it was given, so the service
                // type is preserved and nothing downstream can tell the difference.
                return new DbContextOptionsBuilder(original).AddInterceptors(interceptors).Options;
            }, descriptor.Lifetime);

            hooked++;
        }

        return hooked;
    }

    /// <summary>
    /// On by default in Development only. Turning it on elsewhere is one setting, and the overhead
    /// budget in the README is what makes that a reasonable thing to do.
    /// </summary>
    private static bool IsEnabled(MomusOptions options, IHostEnvironment? environment)
    {
        if (options.Enabled is { } enabled) return enabled;
        if (environment is not null) return environment.IsDevelopment();

        // Nothing told us where we are. Fall back to the variable that usually decides it, and to
        // "off" if even that is missing: being quiet when unsure is the only acceptable default
        // for something that would otherwise switch itself on in production.
        var name = System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                   ?? System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        return string.Equals(name, Environments.Development, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Only finds the environment when the host registered it as an instance, which is not
    /// guaranteed — hence the overload that takes the builder and simply knows.
    /// </summary>
    private static IHostEnvironment? HostEnvironment(IServiceCollection services) =>
        services.FirstOrDefault(d => d.ServiceType == typeof(IHostEnvironment))
            ?.ImplementationInstance as IHostEnvironment;

    /// <summary>Says what Momus did and did not manage to hook, once, at startup.</summary>
    private sealed class StartupReport(int hooked, MomusOptions options, ILogger logger) : IHostedService
    {
        public Task StartAsync(CancellationToken ct)
        {
            if (hooked == 0)
            {
                logger.LogWarning(
                    "Momus found no DbContext to watch. AddMomus() has to be called after your " +
                    "AddDbContext() calls — move it below them in Program.cs. A DbContext built by " +
                    "hand, outside dependency injection, cannot be watched at all.");
            }
            else
            {
                logger.LogInformation(
                    "Momus is watching {Count} DbContext registration(s); connection strings are {Shared}.",
                    hooked, options.SharesConnectionStrings ? "shared with the server" : "not shared");
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
