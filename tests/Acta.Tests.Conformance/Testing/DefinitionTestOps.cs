using System.Collections.Immutable;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Definitions;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Test-support entry points into the Definitions feature: registration through the service (so the
/// descriptor-to-row policy, hash, and write gate run exactly as production) and the override write
/// through the service (so canonicalization and rejection rules apply before the store).
/// </summary>
internal static class DefinitionTestOps
{
    public static async Task<IReadOnlyDictionary<string, int>> RegisterAsync(
        IServiceProvider services,
        short namespaceId,
        DateTime manifestGenerationUtc,
        ImmutableArray<JobDescriptor> descriptors,
        CancellationToken ct
    )
    {
        var stored = await services.GetRequiredService<IDefinitionStore>().GetDefinitionContractsAsync(namespaceId, ct);
        return await services
            .GetRequiredService<DefinitionsService>()
            .RegisterAsync(namespaceId, manifestGenerationUtc, descriptors, stored, ct);
    }

    public static async Task<DefinitionOverrideResult> SetOverridesAsync(
        IServiceProvider services,
        int definitionId,
        int expectedVersion,
        JobDefinitionPolicyOverrides overrides,
        JobControlActor actor,
        string? note,
        CancellationToken ct
    ) =>
        await services
            .GetRequiredService<DefinitionsService>()
            .SetOverridesAsync(definitionId, expectedVersion, overrides, actor.ActorKey, note, ct);
}
