using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace Acta;

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
            storedByName[s.DefinitionName] = s;
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

            var incoming = JobDefinitionRegistration.ContractOf(descriptor);
            if (incoming != existing.Contract)
            {
                drifts.Add(new ContractDrift(descriptor.JobName, existing.Contract, incoming));
            }
        }

        return drifts;
    }
}

/// <summary>
/// Applies <see cref="PayloadContractDriftMode"/> to detected contract drift: fails worker startup
/// under Fail, logs a warning per drift under Warn. The warning is framed as live contract drift,
/// not corruption of already-enqueued rows.
/// </summary>
internal static class ContractDriftPolicy
{
    public static void Apply(PayloadContractDriftMode mode, IReadOnlyList<ContractDrift> drifts, string ns, ILogger log)
    {
        if (drifts.Count == 0)
        {
            return;
        }

        if (mode == PayloadContractDriftMode.Fail)
        {
            var names = string.Join(", ", drifts.Select(d => d.JobName));
            throw new PayloadContractDriftException(
                $"Payload contract drift in namespace '{ns}' for: {names}. "
                    + "Worker startup is blocked because PayloadContractDriftMode is Fail."
            );
        }

        foreach (var drift in drifts)
        {
            if (log.IsEnabled(LogLevel.Warning))
            {
                ActaLog.PayloadContractDrift(
                    log,
                    drift.JobName,
                    ns,
                    $"input type {drift.Stored.InputTypeName} -> {drift.Incoming.InputTypeName}, "
                        + $"input format {drift.Stored.InputFormatName} -> {drift.Incoming.InputFormatName}"
                );
            }
        }
    }
}
