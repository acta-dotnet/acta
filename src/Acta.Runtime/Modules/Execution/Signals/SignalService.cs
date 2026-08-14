using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Workers;
using Microsoft.Extensions.Options;

namespace Acta.Runtime.Modules.Execution.Signals;

/// <summary>
/// Signals feature behavior: the operator raise surface (name canonicalization, inline-size cap,
/// lookup resolution, actor stamping, and the released-to-Ready wake) over the store port.
/// </summary>
internal sealed class SignalService(
    ISignalStore store,
    JobsService jobs,
    WorkerWakeupPublisher wakeupPublisher,
    IOptions<JobsOptions> options
)
{
    private readonly int _maxInlinePayloadBytes = options.Value.MaxInlinePayloadBytes;

    public async ValueTask<JobControlResult> RaiseAsync(
        JobLookup job,
        string name,
        byte valueFormatId,
        byte[]? value,
        string? actorKey,
        CancellationToken ct
    )
    {
        name = IdentifierSyntax.CanonicalizeUserKebab(name, nameof(name), IdentifierSyntax.ExtendedMaxLength);
        if (value is { Length: var valueLength } && valueLength > _maxInlinePayloadBytes)
        {
            throw new PayloadTooLargeException($"signal '{name}' value", valueLength, _maxInlinePayloadBytes);
        }

        var jobId = await jobs.ResolveJobIdAsync(job, ct);
        if (jobId is null)
        {
            return new JobControlResult(0, ControlAction.NotFound, null);
        }

        var input = new JobControlInput(Operator(actorKey), JobEventReasonCode.JobSignalReleased, null);
        var outcome = await store.RaiseSignalAsync(
            new RaiseSignalCommand(jobId.Value, JobCheckpointKindCode.Signal, name, valueFormatId, value, input),
            ct
        );
        var result = new JobControlResult(jobId.Value, (ControlAction)(byte)outcome.Action, outcome.Status);
        if (result is { Action: ControlAction.Applied, Status: JobStatusCode.Ready })
        {
            await wakeupPublisher.WakeAsync(WorkerWakeupChannel.AllWorkerNamespaces, WorkerWakeupReason.WorkAvailable, ct);
        }

        return result;
    }

    private static JobControlActor Operator(string? actorKey) =>
        new(ActorCode.Operator, JobControlActor.SanitizeActorKey(actorKey).Truncate(ActaTextLimits.ActorKey));
}
