using Microsoft.Extensions.DependencyInjection;
using Xunit;

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

    /// <summary>The permanent slot of a scheduled definition, by path; the slot carries no key of its own.</summary>
    public static async Task<long> SlotIdAsync(IDbSession db, string jobNamespace, string jobName, CancellationToken ct)
    {
        var id = await TrySlotIdAsync(db, jobNamespace, jobName, ct);
        Assert.NotNull(id);
        return id!.Value;
    }

    public static async Task<long?> TrySlotIdAsync(IDbSession db, string jobNamespace, string jobName, CancellationToken ct)
    {
        var ns = await db.From<NamespaceEntity>().Where(n => n.NamespaceName == jobNamespace).SingleOrDefaultAsync(ct);
        return ns is null ? null : await TrySlotIdAsync(db, ns.Id, jobName, ct);
    }

    public static async Task<long> SlotIdAsync(IDbSession db, int namespaceId, string jobName, CancellationToken ct)
    {
        var id = await TrySlotIdAsync(db, namespaceId, jobName, ct);
        Assert.NotNull(id);
        return id!.Value;
    }

    public static async Task<long?> TrySlotIdAsync(IDbSession db, int namespaceId, string jobName, CancellationToken ct)
    {
        var definition = await db.From<DefinitionEntity>()
            .Where(d => d.NamespaceId == namespaceId && d.DefinitionName == jobName)
            .SingleOrDefaultAsync(ct);
        if (definition is null)
        {
            return null;
        }

        var slot = await db.From<JobEntity>()
            .Where(j => j.DefinitionId == definition.Id && j.Kind == JobKindCode.PermanentSlot)
            .SingleOrDefaultAsync(ct);
        return slot?.Id;
    }

    /// <summary>A slot's schedules, each with its cursor; a schedule missing its cursor row fails the read.</summary>
    public static async Task<IReadOnlyList<ScheduleRow>> ReadAsync(IDbSession db, long slotId, CancellationToken ct)
    {
        var configs = await db.From<ScheduleEntity>().Where(s => s.JobId == slotId).ToListAsync(ct);
        var ids = configs.Select(c => c.Id).ToList();
        var cursors = await db.From<ScheduleStateEntity>().Where(s => ids.Contains(s.Id)).ToListAsync(ct);
        return [.. configs.Select(c => new ScheduleRow(c, cursors.Single(s => s.Id == c.Id)))];
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
    public string TimeZoneId => Config.TimeZoneId;
    public string? Description => Config.Description;
    public string? PauseMessage => Config.PauseMessage;
    public int Version => Config.Version;
    public DateTime? NextOccurrenceAtUtc => Cursor.NextOccurrenceAtUtc;
    public DateTime? DecidedThroughAtUtc => Cursor.DecidedThroughAtUtc;
    public DateTime? NextEvaluationAtUtc => Cursor.NextEvaluationAtUtc;
    public EvaluationStatusCode EvaluationStatus => Cursor.EvaluationStatus;
}
