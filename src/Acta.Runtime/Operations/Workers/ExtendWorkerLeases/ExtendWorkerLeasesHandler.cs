using System.Globalization;

namespace Acta.Runtime;

/// <summary><see cref="WorkerLifecycle.ExtendWorkerLeasesAsync"/>: one round trip.</summary>
internal sealed class ExtendWorkerLeasesHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<IReadOnlyList<LeaseRenewalRow>> HandleAsync(ExtendWorkerLeasesCommand command, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("ExtendWorkerLeases"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeasedByWorkerId, command.WorkerId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeaseTtlSeconds, command.LeaseTtlSeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.Draining, command.Draining));
            },
            static reader => new LeaseRenewalRow(
                reader.GetInt64(0),
                Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToBoolean(reader.GetValue(2), CultureInfo.InvariantCulture)
            ),
            ct
        );
}
