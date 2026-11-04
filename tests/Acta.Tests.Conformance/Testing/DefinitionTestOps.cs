using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Test-support entry points into the Definitions group: registration through its handler (so the
/// descriptor-to-row policy, hash, and write gate run exactly as production) and the override write
/// through the facade (so canonicalization and rejection rules apply before the write).
/// </summary>
internal static class DefinitionTestOps
{
    /// <summary>The passes <c>WorkerRuntimeInitializer</c> gives a catalog registration.</summary>
    private const int RegistrationPasses = 5;

    /// <summary>
    /// Registers <paramref name="descriptors"/> through the handler a starting worker runs, with the initializer's passes:
    /// a registration that waited out its command timeout behind the other specs' registrations, or lost a race for the
    /// same new rows, runs again, and registering the same descriptors again changes nothing.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, int>> RegisterAsync(
        IServiceProvider services,
        int namespaceId,
        DateTime manifestGenerationUtc,
        ImmutableArray<JobDescriptor> descriptors,
        CancellationToken ct
    )
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var stored = (
                    await services
                        .GetRequiredService<ListDefinitionContractsHandler>()
                        .HandleAsync(new ListDefinitionContractsQuery(namespaceId), ct)
                ).Contracts;
                return (
                    await services
                        .GetRequiredService<RegisterDefinitionsHandler>()
                        .HandleAsync(new RegisterDefinitionsCommand(namespaceId, manifestGenerationUtc, descriptors, stored), ct)
                ).DefinitionIds;
            }
            catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException && attempt < RegistrationPasses)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), ct);
            }
        }
    }

    public static async Task<UpdateDefinitionOverridesResponse> UpdateOverridesAsync(
        IServiceProvider services,
        string jobNamespace,
        string jobName,
        int expectedVersion,
        DefinitionPolicyOverrides overrides,
        string? actor,
        string? reasonMessage,
        CancellationToken ct
    ) =>
        await services
            .GetRequiredService<IDefinitions>()
            .UpdateOverridesAsync(jobNamespace, jobName, expectedVersion, overrides, reasonMessage, actor, ct);
}
