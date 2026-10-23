using System.Data.Common;

namespace Acta;

/// <summary>What an operation binds for the event it writes: the human message and the details payload.</summary>
internal static class OperatorAudit
{
    /// <summary>
    /// Binds the operator's event: the reason message cut to its column as <c>event_message</c>, and the principal as
    /// <c>details.actor</c>. A public verb never takes an actor kind from the caller, so the actor is always the operator.
    /// </summary>
    public static void AddOperatorAudit(this DbCommand cmd, ISqlDialect dialect, string? actor, string? reasonMessage) =>
        cmd.AddEventFields(
            dialect,
            reasonMessage.Truncate(ActaTextLimits.EventMessage),
            EventDetails.Write(("actor", JobControlActor.Operator(actor)))
        );

    /// <summary>Binds <c>event_message</c> and the <c>details</c> pair, the format id following the payload.</summary>
    public static void AddEventFields(this DbCommand cmd, ISqlDialect dialect, string? message, byte[]? details)
    {
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.EventMessage, message));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.DetailsFormatId, EventDetails.FormatId(details)));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.Details, details));
    }
}
