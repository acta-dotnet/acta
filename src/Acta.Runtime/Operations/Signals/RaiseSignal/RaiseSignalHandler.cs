using Microsoft.Extensions.Options;

namespace Acta.Runtime;

/// <summary>
/// The operator raise behind <c>IJobs.RaiseSignalAsync</c>: canonicalizes the name, caps the payload, resolves
/// the job, raises the slot as the operator, and wakes the workers when the raise released the job to Ready.
/// </summary>
internal sealed class RaiseSignalHandler(
    IDbSession session,
    ISqlDialect dialect,
    JobLookupResolution jobs,
    WorkerWakeupPublisher wakeup,
    IJobPayloadSerializerRegistry serializers,
    IOptions<JobsOptions> options
)
{
    private readonly int _maxInlinePayloadBytes = options.Value.MaxInlinePayloadBytes;

    public async ValueTask<RaiseSignalResponse> HandleAsync(RaiseSignalCommand command, CancellationToken ct)
    {
        var name = IdentifierSyntax.CanonicalizeUserKebab(command.Name, "name", IdentifierSyntax.ExtendedMaxLength);
        var value = command.Value;
        if (value.Data.Length > _maxInlinePayloadBytes)
        {
            throw new PayloadTooLargeException($"signal '{name}' value", value.Data.Length, _maxInlinePayloadBytes);
        }

        if (await jobs.ResolveAsync(command.Job, ct) is not { } jobId)
        {
            return new RaiseSignalResponse(0, ControlAction.NotFound, null, null);
        }

        var input = new JobControlInput(JobControlActor.Operator(command.ActorKey), JobEventReasonCode.JobSignalReleased, null);
        var row = await RaiseSlotAsync(
            new SignalSlotRaise(
                jobId,
                JobCheckpointKindCode.Signal,
                name,
                value.Format.Id,
                value.IsNone ? null : value.Data.ToArray(),
                input
            ),
            ct
        );
        await JobControl.WakeIfReadyAsync(wakeup, row.Action, row.Status, ct);
        return new RaiseSignalResponse(jobId, row.Action, row.Status, null);
    }

    /// <summary>
    /// The typed raise: <paramref name="value"/> serializes through the registry, which honours UseJsonPayloads,
    /// rather than the reflection-based static helper.
    /// </summary>
    public ValueTask<RaiseSignalResponse> HandleAsync<T>(JobLookup job, string name, T value, string? actorKey, CancellationToken ct) =>
        HandleAsync(
            new RaiseSignalCommand(
                job,
                name,
                serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(value ?? throw new ArgumentNullException(nameof(value))),
                actorKey
            ),
            ct
        );

    /// <summary><see cref="Signals.RaiseSignalAsync"/>: one routine call for any slot kind, the operator's raise among them.</summary>
    internal Task<JobControlActionRow> RaiseSlotAsync(SignalSlotRaise raise, CancellationToken ct) =>
        session.ExecuteOneAsync(
            new StoreCommand("RaiseSignal"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.JobId, raise.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.KindCode, raise.Kind));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.Name, raise.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.ValueFormatId, raise.ValueFormatId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.Value, raise.Value));
                JobControl.BindActor(cmd, dialect, raise.Input);
            },
            DbProjectionResolver.Resolve<JobControlActionRow>(),
            ct
        );
}
