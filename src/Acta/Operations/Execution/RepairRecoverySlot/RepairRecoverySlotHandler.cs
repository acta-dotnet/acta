using System.Globalization;

namespace Acta;

/// <summary><see cref="Execution.RepairRecoverySlotAsync"/>: one routine call; no row means the slot is missing.</summary>
internal sealed class RepairRecoverySlotHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<RepairRecoverySlotResponse> HandleAsync(RepairRecoverySlotCommand command, CancellationToken ct)
    {
        var rows = await session.ExecuteAsync(
            new StoreCommand("RepairRecoverySlot"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.NamespaceId, command.NamespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(new DbParameterSpec("p_job_id", command.JobId, DbKind.Int64)));
            },
            // Widened through Convert: SQLite hands every integer back as Int64.
            static reader =>
                (RecoverySlotRepair)Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
            ct
        );
        return new RepairRecoverySlotResponse(rows.Count == 0 ? RecoverySlotRepair.Missing : rows[0]);
    }
}
