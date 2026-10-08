using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acta.Runtime;

/// <summary>Registers the one durable provider an Acta service collection may have.</summary>
internal static class ActaProviderRegistration
{
    /// <summary>
    /// Registers the selected provider: its info, validated options, dialect, session, SQL catalog, and bootstrap.
    /// Each provider package's <c>Use{Provider}</c> is this call with its own types; <paramref name="dialect"/>
    /// builds a dialect that needs more than its constructor's services. The type parameters keep the constructors
    /// the container activates through them; without the annotations Native AOT trims those constructors and the
    /// host fails to start.
    /// </summary>
    public static IActaBuilder Use<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TDialect,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TBootstrap
    >(IActaBuilder builder, DbProvider provider, Action<TOptions> configure, Func<IServiceProvider, TDialect>? dialect = null)
        where TOptions : SqlProviderOptions
        where TDialect : class, ISqlDialect
        where TBootstrap : class, IProviderBootstrap
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var services = builder.Services;
        Add(services, new ActaProviderInfo(provider));
        services.Configure(configure);
        services.AddOptions<TOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TOptions>, SqlProviderOptionsValidator<TOptions>>());
        services.AddSingleton<SqlProviderOptions>(static sp => sp.GetRequiredService<IOptions<TOptions>>().Value);

        if (dialect is null)
        {
            services.AddSingleton<TDialect>();
        }
        else
        {
            services.AddSingleton(dialect);
        }

        services.AddSingleton<ISqlDialect>(static sp => sp.GetRequiredService<TDialect>());
        services.AddSingleton(static sp => new SqlResourceCatalog(
            sp.GetRequiredService<ISqlDialect>().DialectToken,
            sp.GetRequiredService<SqlProviderOptions>().Schema
        ));
        // GetService, not GetRequiredService: a bare host may register no logging at all, and the
        // session falls back to the null logger rather than failing to resolve.
        services.AddSingleton(static sp => new DbSession(
            sp.GetRequiredService<SqlProviderOptions>(),
            sp.GetRequiredService<ISqlDialect>(),
            sp.GetRequiredService<SqlResourceCatalog>(),
            sp.GetService<ILogger<DbSession>>()
        ));
        services.AddSingleton<IDbSession>(static sp => sp.GetRequiredService<DbSession>());
        services.AddSingleton<IProviderBootstrap, TBootstrap>();
        return builder;
    }

    public static void Add(IServiceCollection services, ActaProviderInfo provider)
    {
        var existing = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(ActaProviderInfo));
        if (existing is not null)
        {
            var existingName = Describe(existing);
            var requestedName = provider.Provider.ToString();
            var action = string.Equals(existingName, requestedName, StringComparison.Ordinal)
                ? $"register '{requestedName}' again"
                : $"register '{requestedName}'";
            throw new InvalidOperationException(
                $"Acta durable provider '{existingName}' is already registered; cannot {action}. Configure exactly one durable provider per service collection."
            );
        }

        services.AddSingleton(provider);
    }

    public static IReadOnlyList<string> FindAll(IServiceCollection services) =>
        services.Where(descriptor => descriptor.ServiceType == typeof(ActaProviderInfo)).Select(Describe).ToArray();

    private static string Describe(ServiceDescriptor descriptor) =>
        descriptor.ImplementationInstance is ActaProviderInfo info ? info.Provider.ToString() : "unknown";
}
