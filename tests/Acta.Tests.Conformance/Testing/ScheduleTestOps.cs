using Acta.Modules.Execution.Schedules;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Test-support entry point into the Schedules feature: the startup reconcile upsert through the
/// store port with production slot-ref allocation, returning the definition-to-slot map.
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
            .GetRequiredService<IScheduleStore>()
            .RegisterScheduledJobsAsync(new RegisterScheduledJobsCommand(definitions, slotRefs), ct);
        return slots.ToDictionary(s => s.DefinitionId, s => s.SlotId);
    }
}
