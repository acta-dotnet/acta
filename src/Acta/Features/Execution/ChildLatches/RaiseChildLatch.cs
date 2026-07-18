using Acta.Features.Jobs;
using Acta.Features.Signals;
using Acta.Payloads;

namespace Acta.Features.Execution.ChildLatches;

/// <summary>
/// Raises a child's terminal-outcome latch (<c>sys.child.{childId}</c>) on its parent: a composite
/// over the existing <c>raise_signal</c> routine with no SQL of its own. The slot is upserted Set
/// with the JSON envelope, a Suspended parent is released to Ready, and a terminal or missing parent
/// is a clean no-op. Serves the C#-side raise sites (operator cancel, reclaim, maintenance
/// backstop); <c>complete_execution</c> raises in-transaction itself on the hot path. Returns true
/// when the raise released the parent.
/// </summary>
internal static class RaiseChildLatch
{
    public const string NamePrefix = "sys.child.";

    public static async Task<bool> Run(
        ISignalStore signals,
        long childJobId,
        long parentJobId,
        JobStatusCode childStatus,
        CancellationToken ct
    )
    {
        var envelope = ChildOutcomeEnvelope.Write(childJobId, childStatus);
        var input = new JobControlInput(new JobControlActor(JobActorCode.Sys), JobEventReasonCode.JobSignalReleased, null);
        var outcome = await signals.RaiseSignalAsync(
            new RaiseSignalCommand(
                parentJobId,
                JobCheckpointKindCode.ChildLatch,
                NamePrefix + childJobId,
                JobPayloadFormat.Json.Id,
                envelope,
                input
            ),
            ct
        );
        return outcome.Action == JobControlActionInternal.Applied && outcome.Status == JobStatusCode.Ready;
    }
}
