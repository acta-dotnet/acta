using Microsoft.Extensions.Logging;

namespace Acta.Runtime.Modules.Execution.Definitions;

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
            log.LogWarning(
                "Payload contract drift for job {JobName} in namespace {Namespace}: {Detail}. Enqueues made from now on use the new contract.",
                drift.JobName,
                ns,
                $"input type {drift.Stored.InputTypeName} -> {drift.Incoming.InputTypeName}, "
                    + $"input format {drift.Stored.InputFormatName} -> {drift.Incoming.InputFormatName}"
            );
        }
    }
}
