namespace Acta.Runtime;

/// <summary>
/// Takes the first free slot of a counted key in one round trip whatever the limit: the SQL enumerates
/// the slots and inserts the first free one. Null when every slot is held or a racer took the chosen one.
/// </summary>
internal sealed class AcquireSlotHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<LockToken?> HandleAsync(AcquireSlotCommand command, CancellationToken ct)
    {
        // A limit counts the holders that may run at once, so it is at least one; below that it names no slot,
        // and the dialects' slot enumerations would answer it differently.
        ArgumentOutOfRangeException.ThrowIfLessThan(command.Limit, 1, "limit");
        var holdToken = Guid.NewGuid();
        var key = await session.ExecuteSingleAsync(
            new StoreCommand("AcquireSlot"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LockKeyPrefix, command.KeyPrefix));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.SlotCount, command.Limit));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lock.JobId, command.OwnerJobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeaseTtlSeconds, (int)command.Ttl.TotalSeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lock.HoldToken, holdToken));
            },
            // The SQL picks the slot, so the taken key comes back rather than being recomposed here;
            // extend and release then work on it like any other lock key.
            static reader =>
                reader.GetString(0),
            ct
        );
        return key is null ? null : new LockToken(key, holdToken);
    }
}
