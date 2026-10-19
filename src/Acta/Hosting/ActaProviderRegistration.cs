using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>Registers the one durable provider an Acta service collection may have.</summary>
internal static class ActaProviderRegistration
{
    /// <summary>
    /// Registers the selected provider: its info, validated options, dialect, session, SQL catalog, and bootstrap.
    /// Each provider package's <c>Use{Provider}</c> is this call with its own types. The type parameters keep the
    /// constructors the container activates through them; without the annotations Native AOT trims those constructors
    /// and the host fails to start.
    /// </summary>
    public static IActaBuilder Use<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TDialect,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TBootstrap
    >(IActaBuilder builder, DbProvider provider, Action<SqlProviderOptions> configure)
        where TDialect : class, ISqlDialect
        where TBootstrap : class, IProviderBootstrap
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var services = builder.Services;
        Add(services, new SelectedProvider(provider));
        services.Configure(configure);
        services.AddOptions<SqlProviderOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SqlProviderOptions>, SqlProviderOptionsValidator>());
        services.AddSingleton(static sp => sp.GetRequiredService<IOptions<SqlProviderOptions>>().Value);
        services.AddSingleton<TDialect>();
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

    public static void Add(IServiceCollection services, SelectedProvider provider)
    {
        var existing = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(SelectedProvider));
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
        services.Where(descriptor => descriptor.ServiceType == typeof(SelectedProvider)).Select(Describe).ToArray();

    private static string Describe(ServiceDescriptor descriptor) =>
        descriptor.ImplementationInstance is SelectedProvider info ? info.Provider.ToString() : "unknown";
}

/// <summary>
/// Validates <see cref="SqlProviderOptions"/> at host startup (paired with <c>ValidateOnStart</c>). Mirrors
/// <c>JobsOptionsValidator</c>'s aggregate-and-report shape.
/// </summary>
internal sealed class SqlProviderOptionsValidator : IValidateOptions<SqlProviderOptions>
{
    public ValidateOptionsResult Validate(string? name, SqlProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        const string prefix = nameof(SqlProviderOptions);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            failures.Add($"{prefix}.ConnectionString must not be blank.");
        }

        if (
            string.IsNullOrEmpty(options.Schema)
            || options.Schema.Length > SqlIdentifierSyntax.BareIdentifierMaxLength
            || !SqlIdentifierSyntax.IsBareIdentifier(options.Schema)
        )
        {
            failures.Add(
                $"{prefix}.Schema must be a bare SQL identifier (`[a-z][a-z0-9_]*`) no longer than {SqlIdentifierSyntax.BareIdentifierMaxLength} characters."
            );
        }

        if (options.DeadlockRetryAttempts is < 1 or > SqlProviderOptions.MaxDeadlockRetryAttempts)
        {
            failures.Add(
                $"{prefix}.DeadlockRetryAttempts must be between 1 and {SqlProviderOptions.MaxDeadlockRetryAttempts}: set 1 to disable retry."
            );
        }

        if (options.CommandTimeout <= TimeSpan.Zero)
        {
            failures.Add(
                $"{prefix}.CommandTimeout must be > 0: 0 is ADO.NET's infinite-timeout sentinel, which conflicts with the lease model."
            );
        }
        else if (options.CommandTimeout.TotalSeconds > int.MaxValue)
        {
            failures.Add($"{prefix}.CommandTimeout must not exceed {int.MaxValue} seconds, the ADO.NET command-timeout limit.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
