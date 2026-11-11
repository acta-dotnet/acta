using System.Data.Common;

namespace Acta;

/// <summary>What an operation binds for the event it writes: the human message, the actor, and the details payload.</summary>
internal static class OperatorAudit
{
    /// <summary>
    /// Binds the operator's event: the reason message cut to its column as <c>event_message</c>, the principal as
    /// <c>actor</c>, and no details. A public verb never takes an actor kind from the caller, so the actor is always the
    /// operator.
    /// </summary>
    public static void AddOperatorAudit(this DbCommand cmd, ISqlDialect dialect, string? actor, string? reasonMessage) =>
        cmd.AddEventFields(dialect, reasonMessage.Truncate(ActaTextLimits.EventMessage), JobControlActor.Operator(actor), null);

    /// <summary>Binds <c>event_message</c>, <c>actor</c>, and the <c>details</c> pair.</summary>
    public static void AddEventFields(this DbCommand cmd, ISqlDialect dialect, string? message, string? actor, byte[]? details)
    {
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.EventMessage, message));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.Actor, actor));
        cmd.AddEventDetails(dialect, details);
    }

    /// <summary>Binds the <c>details</c> pair, the format id following the payload.</summary>
    public static void AddEventDetails(this DbCommand cmd, ISqlDialect dialect, byte[]? details)
    {
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.DetailsFormatId, EventDetails.FormatId(details)));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.Details, details));
    }
}
