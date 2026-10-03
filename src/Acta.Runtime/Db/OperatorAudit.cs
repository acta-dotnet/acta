using System.Data.Common;

namespace Acta.Runtime;

/// <summary>The audit every operator verb writes onto its event: the operator, their key, and the reason.</summary>
internal static class OperatorAudit
{
    /// <summary>
    /// Binds the actor code, actor key, and reason message in that order, the key and reason cut to their
    /// columns. A public verb never takes an actor from the caller, so the actor is always the operator.
    /// </summary>
    public static void AddOperatorAudit(this DbCommand cmd, ISqlDialect dialect, string? actorKey, string? reasonMessage)
    {
        var actor = JobControlActor.Operator(actorKey);
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorCode, actor.ActorCode));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorKey, actor.ActorKey));
        cmd.Parameters.Add(
            dialect.CreateParameter(ActaSchema.JobEvent.ReasonMessage, reasonMessage.Truncate(ActaTextLimits.ReasonMessage))
        );
    }
}
