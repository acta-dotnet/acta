using Microsoft.Extensions.Options;

namespace Acta.Runtime;

/// <summary><see cref="IJobs.UpdateJobInputAsync"/>: caps the payload, then replaces the input of a job that is not in flight.</summary>
internal sealed class UpdateJobInputHandler(
    JobLookupResolution lookup,
    IDbSession session,
    ISqlDialect dialect,
    IOptions<JobsOptions> options
)
{
    private readonly int _maxInlinePayloadBytes = options.Value.MaxInlinePayloadBytes;

    public async ValueTask<UpdateJobInputResponse> HandleAsync(UpdateJobInputCommand command, CancellationToken ct)
    {
        var input = command.Input;
        if (input.Data.Length > _maxInlinePayloadBytes)
        {
            throw new PayloadTooLargeException("update input", input.Data.Length, _maxInlinePayloadBytes);
        }

        if (await lookup.ResolveAsync(command.Job, ct) is not { } jobId)
        {
            return new UpdateJobInputResponse(0, ControlAction.NotFound, null, null);
        }

        var control = JobControl.Operator(command.ReasonMessage, command.ActorKey);
        var row = await session.ExecuteOneAsync(
            new StoreCommand("UpdateJobInput"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.Id, jobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.InputFormatId, input.Format.Id));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.Input, input.IsNone ? null : input.Data.ToArray()));
                JobControl.BindActor(cmd, dialect, control);
            },
            DbProjectionResolver.Resolve<UpdateJobInputRow>(),
            ct
        );
        return new UpdateJobInputResponse(jobId, row.Action, row.Status, null);
    }
}
