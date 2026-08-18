using System.Globalization;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Signals;

namespace Acta.Runtime.Modules.Execution.ChildLatches;

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
        // Write side of the child-latch checkpoint key. The name is persisted in the ledger and matched
        // as text by JobContext.WaitChildAsync, in another process and possibly another culture, so the
        // two renderings must agree byte for byte; the invariant culture is stated on both sides rather
        // than inherited from whatever the ambient one happens to be.
        var slotName = NamePrefix + childJobId.ToString(CultureInfo.InvariantCulture);
        var envelope = ChildOutcomeEnvelope.Write(childJobId, childStatus);
        var input = new JobControlInput(new JobControlActor(ActorCode.Sys), JobEventReasonCode.JobSignalReleased, null);
        var outcome = await signals.RaiseSignalAsync(
            new RaiseSignalCommand(parentJobId, JobCheckpointKindCode.ChildLatch, slotName, JobPayloadFormat.Json.Id, envelope, input),
            ct
        );
        return outcome.Action == JobControlActionInternal.Applied && outcome.Status == JobStatusCode.Ready;
    }
}
