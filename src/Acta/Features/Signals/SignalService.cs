using Acta.Features.Jobs;
using Acta.Features.Shared;
using Acta.Features.Workers;
using Acta.Payloads;
using Microsoft.Extensions.Options;

namespace Acta.Features.Signals;

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
            return new JobControlResult(0, JobControlAction.NotFound, null);
        }

        var input = new JobControlInput(Operator(actorKey), JobEventReasonCode.JobSignalReleased, null);
        var outcome = await store.RaiseSignalAsync(
            new RaiseSignalCommand(jobId.Value, JobCheckpointKindCode.Signal, name, valueFormatId, value, input),
            ct
        );
        var result = new JobControlResult(jobId.Value, (JobControlAction)(byte)outcome.Action, outcome.Status);
        if (result is { Action: JobControlAction.Applied, Status: JobStatusCode.Ready })
        {
            await wakeupPublisher.WakeAsync(WorkerWakeupChannel.AllWorkerNamespaces, WorkerWakeupReason.WorkAvailable, ct);
        }

        return result;
    }

    private static JobControlActor Operator(string? actorKey) =>
        new(JobActorCode.Operator, JobControlActor.SanitizeActorKey(actorKey).Truncate(ActaTextLimits.ActorKey));
}
