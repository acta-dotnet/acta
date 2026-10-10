using System.Data.Common;

namespace Acta;

/// <summary>The audit every operator verb writes onto its event: the operator, their name, and the reason.</summary>
internal static class OperatorAudit
{
    /// <summary>
    /// Binds the actor code, actor, and reason message in that order, the actor and reason cut to their
    /// columns. A public verb never takes an actor from the caller, so the actor is always the operator.
    /// </summary>
    public static void AddOperatorAudit(this DbCommand cmd, ISqlDialect dialect, string? actor, string? reasonMessage)
    {
        var stamped = JobControlActor.Operator(actor);
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.ActorCode, stamped.ActorCode));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.Actor, stamped.Actor));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.ReasonMessage, reasonMessage.Truncate(ActaTextLimits.ReasonMessage)));
    }
}
