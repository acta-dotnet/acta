namespace Acta;

/// <summary>
/// Takes the first free slot of a counted key in one round trip whatever the limit: the SQL enumerates
/// the slots and inserts the first free one. It answers no row when every slot is held, and a null key when a
/// racer took the free slot first.
/// </summary>
internal sealed class AcquireLockSlotHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<AcquireLockSlotResponse> HandleAsync(AcquireLockSlotCommand command, CancellationToken ct)
    {
        // A limit counts the holders that may run at once, so it is at least one; below that it names no slot,
        // and the dialects' slot enumerations would answer it differently.
        ArgumentOutOfRangeException.ThrowIfLessThan(command.Limit, 1, "limit");
        var holdToken = Guid.NewGuid();
        var rows = await session.ExecuteAsync(
            new StoreCommand("AcquireLockSlot"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LockKeyPrefix, command.KeyPrefix));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LockSlotCount, command.Limit));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lock.JobId, command.OwnerJobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeaseTtlSeconds, (int)command.Ttl.TotalSeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lock.HoldToken, holdToken));
            },
            // The SQL picks the slot, so the taken key comes back rather than being recomposed here;
            // extend and release then work on it like any other lock key.
            static reader =>
                reader.IsDBNull(0) ? null : reader.GetString(0),
            ct
        );
        return rows switch
        {
            [{ } key] => new AcquireLockSlotResponse(new LockToken(key, holdToken), LostRace: false),
            [null] => new AcquireLockSlotResponse(null, LostRace: true),
            _ => new AcquireLockSlotResponse(null, LostRace: false),
        };
    }
}
