using Microsoft.Extensions.DependencyInjection;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Test-support entry point into the Schedules feature: the startup reconcile upsert through the
/// store port with production slot-ref allocation, returning the definition-to-slot map, and the read
/// that pairs each schedule with its cursor.
/// </summary>
internal static class ScheduleTestOps
{
    public static async Task<IReadOnlyDictionary<int, long>> RegisterAsync(
        IServiceProvider services,
        IReadOnlyList<DefinitionSchedules> definitions,
        CancellationToken ct
    )
    {
        var slotRefs = new Guid[definitions.Count];
        for (var i = 0; i < slotRefs.Length; i++)
        {
            slotRefs[i] = JobRef.New().Value;
        }

        var slots = await services
            .GetRequiredService<RegisterSchedulesHandler>()
            .HandleAsync(new RegisterSchedulesCommand(definitions[0].NamespaceId, definitions, slotRefs, null), ct);
        return slots.Slots.ToDictionary(s => s.DefinitionId, s => s.SlotId);
    }

    /// <summary>A slot's schedules, each with its cursor; a schedule missing its cursor row fails the read.</summary>
    public static async Task<IReadOnlyList<ScheduleRow>> ReadAsync(IDbSession db, long slotId, CancellationToken ct)
    {
        var configs = await db.From<ScheduleEntity>().Where(s => s.JobId == slotId).ToListAsync(ct);
        var cursors = await db.From<ScheduleStateEntity>().Where(s => s.JobId == slotId).ToListAsync(ct);
        return [.. configs.Select(c => new ScheduleRow(c, cursors.Single(s => s.ScheduleName == c.ScheduleName)))];
    }
}

/// <summary>One schedule as the specs assert on it: the config row and its cursor row.</summary>
internal sealed record ScheduleRow(ScheduleEntity Config, ScheduleStateEntity Cursor)
{
    public string ScheduleName => Config.ScheduleName;
    public int DefinitionId => Config.DefinitionId;
    public ScheduleOriginCode Origin => Config.Origin;
    public ScheduleStatusCode Status => Config.Status;
    public DateTime? PausedUntilUtc => Config.PausedUntilUtc;
    public ScheduleExpressionKindCode ExpressionKind => Config.ExpressionKind;
    public MisfireStrategyCode MisfireStrategy => Config.MisfireStrategy;
    public string Expression => Config.Expression;
    public string? ExpressionOverride => Config.ExpressionOverride;
    public string ExpressionEffective => Config.ExpressionEffective;
    public string TimeZoneId => Config.TimeZoneId;
    public string? TimeZoneIdOverride => Config.TimeZoneIdOverride;
    public string TimeZoneIdEffective => Config.TimeZoneIdEffective;
    public string? Description => Config.Description;
    public string? ChangeMessage => Config.ChangeMessage;
    public int Version => Config.Version;
    public DateTime? NextOccurrenceAtUtc => Cursor.NextOccurrenceAtUtc;
    public DateTime? LastAdvancedOccurrenceAtUtc => Cursor.LastAdvancedOccurrenceAtUtc;
}
