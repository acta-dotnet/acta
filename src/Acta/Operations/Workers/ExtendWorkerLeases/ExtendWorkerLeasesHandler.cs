namespace Acta;

/// <summary><see cref="WorkersOperations.ExtendWorkerLeasesAsync"/>: one round trip.</summary>
internal sealed class ExtendWorkerLeasesHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ExtendWorkerLeasesResponse> HandleAsync(ExtendWorkerLeasesCommand command, CancellationToken ct)
    {
        var rows = await session.ExecuteAsync(
            new StoreCommand("ExtendWorkerLeases"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeasedByWorkerId, command.WorkerId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeaseTtlSeconds, command.LeaseTtlSeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.Draining, command.Draining));
                dialect.BindHeldLeases(cmd, command.Executions, command.Locks);
                // Last: PostgreSQL routine arguments are positional and this one ends extend_worker_leases' list.
                cmd.Parameters.Add(
                    dialect.CreateParameter(
                        new DbParameterSpec("p_recovery_interval_seconds", command.RecoveryIntervalSeconds, DbKind.Int32)
                    )
                );
            },
            DbProjectionResolver.Resolve<ExtendWorkerLeasesRow>(),
            ct
        );

        List<ExtendWorkerLeasesLease> leases = [];
        List<string> renewedLockKeys = [];
        DateTime? definitionsModifiedAtUtc = null;
        var recoveryClaimed = false;
        foreach (var row in rows)
        {
            if (row.JobId is { } jobId)
            {
                leases.Add(new ExtendWorkerLeasesLease(jobId, row.ExecutionNumber!.Value, row.Renewed));
            }
            else if (row.LockKey is { } lockKey)
            {
                renewedLockKeys.Add(lockKey);
            }
            else
            {
                definitionsModifiedAtUtc = row.DefinitionsModifiedAtUtc;
                recoveryClaimed = row.RecoveryClaimed;
            }
        }

        return new ExtendWorkerLeasesResponse(leases, renewedLockKeys, definitionsModifiedAtUtc, recoveryClaimed);
    }
}
