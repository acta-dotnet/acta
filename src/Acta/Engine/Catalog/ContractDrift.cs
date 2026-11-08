using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace Acta;

/// <summary>
/// One contract change found at registration: a stored definition whose contract columns differ from the incoming
/// descriptor.
/// </summary>
internal sealed record ContractDrift(string JobName, DefinitionContract Stored, DefinitionContract Incoming);

/// <summary>
/// Compares incoming descriptor contracts against the stored rows and reports the changes.
/// Pure: callers decide whether to warn or fail based on <see cref="PayloadContractDriftMode"/>.
/// </summary>
internal static class ContractDriftDetector
{
    public static IReadOnlyList<ContractDrift> Detect(
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
/// Applies <see cref="PayloadContractDriftMode"/> to detected contract drift: fails the deploy
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
                    + "The deploy is refused because PayloadContractDriftMode is Fail; nothing was written."
            );
        }

        foreach (var drift in drifts)
        {
            if (log.IsEnabled(LogLevel.Warning))
            {
                ActaLog.PayloadContractDrift(log, drift.JobName, ns, Changes(drift.Stored, drift.Incoming));
            }
        }
    }

    /// <summary>Each contract part that differs, with its stored and its new value, such as <c>input type A -> B</c>.</summary>
    private static string Changes(DefinitionContract stored, DefinitionContract incoming)
    {
        var changes = new List<string>(4);
        if (stored.InputTypeName != incoming.InputTypeName)
        {
            changes.Add($"input type {stored.InputTypeName} -> {incoming.InputTypeName}");
        }
        if (stored.InputFormatId != incoming.InputFormatId || stored.InputFormatName != incoming.InputFormatName)
        {
            changes.Add($"input format {stored.InputFormatName} -> {incoming.InputFormatName}");
        }
        if (stored.ResultTypeName != incoming.ResultTypeName)
        {
            changes.Add($"result type {stored.ResultTypeName ?? "none"} -> {incoming.ResultTypeName ?? "none"}");
        }
        if (stored.ResultFormatId != incoming.ResultFormatId || stored.ResultFormatName != incoming.ResultFormatName)
        {
            changes.Add($"result format {stored.ResultFormatName} -> {incoming.ResultFormatName}");
        }
        return string.Join(", ", changes);
    }
}
