using System.Data.Common;

namespace Acta.Runtime;

/// <summary>What the acknowledge and resolve verbs share: their routines take the same arguments.</summary>
internal static class AlertControl
{
    /// <summary>Binds the alert ref, then the operator audit, its reason naming the alert.</summary>
    public static void Bind(DbCommand cmd, ISqlDialect dialect, AlertRef alertRef, string? reasonMessage, string? actorKey)
    {
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.AlertRef, alertRef.Value));
        cmd.AddOperatorAudit(dialect, actorKey, reasonMessage is null ? $"alert {alertRef}" : $"alert {alertRef}: {reasonMessage}");
    }
}
