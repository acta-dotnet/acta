using Microsoft.Extensions.DependencyInjection;

namespace Acta.Runtime.Hosting;

/// <summary>Provider-neutral runtime facts supplied by the selected durable provider package.</summary>
internal sealed record ActaProviderInfo(DbProvider Provider, bool SupportsRoutines);

/// <summary>Owns the single durable-provider invariant for one Acta service collection.</summary>
internal static class ActaProviderRegistration
{
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
