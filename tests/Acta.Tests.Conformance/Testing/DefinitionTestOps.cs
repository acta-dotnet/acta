using System.Collections.Immutable;
using Acta.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Test-support entry points into the Definitions group: registration through its handler (so the
/// descriptor-to-row policy, hash, and write gate run exactly as production) and the override write
/// through the facade (so canonicalization and rejection rules apply before the write).
/// </summary>
internal static class DefinitionTestOps
{
    public static async Task<IReadOnlyDictionary<string, int>> RegisterAsync(
        IServiceProvider services,
        int namespaceId,
        DateTime manifestGenerationUtc,
        ImmutableArray<JobDescriptor> descriptors,
        CancellationToken ct
    )
    {
        var stored = await services
            .GetRequiredService<GetDefinitionContractsHandler>()
            .HandleAsync(new GetDefinitionContractsQuery(namespaceId), ct);
        return await services
            .GetRequiredService<RegisterJobDefinitionsHandler>()
            .HandleAsync(new RegisterJobDefinitionsCommand(namespaceId, manifestGenerationUtc, descriptors, stored), ct);
    }

    public static async Task<SetJobDefinitionOverridesResponse> UpdateOverridesAsync(
        IServiceProvider services,
        string jobNamespace,
        string jobName,
        int expectedVersion,
        JobDefinitionPolicyOverrides overrides,
        JobControlActor actor,
        string? reasonMessage,
        CancellationToken ct
    ) =>
        await services
            .GetRequiredService<IDefinitions>()
            .UpdateOverridesAsync(jobNamespace, jobName, expectedVersion, overrides, reasonMessage, actor.ActorKey, ct);
}
