using System.Collections.Immutable;
using Acta.Configuration;
using Acta.Features.Definitions;

namespace Acta.Features.Definitions;

/// <summary>
/// One eligible contract change found at registration: a definition whose stored generation is at
/// or below the incoming generation and whose contract columns differ from the incoming descriptor.
/// </summary>
internal sealed record ContractDrift(string JobName, DefinitionContract Stored, DefinitionContract Incoming);

/// <summary>
/// Compares incoming descriptor contracts against the stored rows and reports eligible changes.
/// Pure: callers decide whether to warn or fail based on <see cref="PayloadContractDriftMode"/>.
/// </summary>
internal static class ContractDriftDetector
{
    public static IReadOnlyList<ContractDrift> Detect(
        DateTime incomingGenerationUtc,
        ImmutableArray<JobDescriptor> descriptors,
        IReadOnlyCollection<StoredDefinitionContract> stored
    )
    {
        if (descriptors.IsDefaultOrEmpty || stored.Count == 0)
        {
            return [];
        }

        var storedByName = new Dictionary<string, StoredDefinitionContract>(stored.Count, StringComparer.Ordinal);
        foreach (var s in stored)
        {
            storedByName[s.Name] = s;
        }

        var drifts = new List<ContractDrift>();
        foreach (var descriptor in descriptors)
        {
            if (!storedByName.TryGetValue(descriptor.JobName, out var existing))
            {
                continue;
            }

            if (incomingGenerationUtc < existing.ManifestGenerationAtUtc)
            {
                continue;
            }

            var incoming = DefinitionsService.ContractOf(descriptor);
            if (incoming != existing.Contract)
            {
                drifts.Add(new ContractDrift(descriptor.JobName, existing.Contract, incoming));
            }
        }

        return drifts;
    }
}
