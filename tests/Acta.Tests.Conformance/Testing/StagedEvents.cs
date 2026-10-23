using Xunit;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Stages one <c>events</c> row for a spec that needs the input a writer would leave. The test insert leaves every
/// server-defaulted column at its default, so the details pair and a chosen stamp are written by updates after it.
/// </summary>
internal static class StagedEvents
{
    public static async Task<long> InsertAsync(
        IDbSession db,
        EventEntity row,
        byte[]? details,
        CancellationToken ct,
        DateTime? createdAtUtc = null
    )
    {
        var id = await db.From<EventEntity>().InsertAsync<long>(row, ct);

        // A staged row names no worker, so (0, id) is its key.
        if (details is not null)
        {
            var formatId = EventDetails.FormatId(details);
            Assert.Equal(
                1,
                await db.From<EventEntity>()
                    .Where(e => e.WorkerId == 0 && e.Id == id)
                    .UpdateOnlyAsync(() => new EventEntity { DetailsFormatId = formatId, Details = details }, ct)
            );
        }

        if (createdAtUtc is { } stamp)
        {
            Assert.Equal(
                1,
                await db.From<EventEntity>()
                    .Where(e => e.WorkerId == 0 && e.Id == id)
                    .UpdateOnlyAsync(() => new EventEntity { CreatedAtUtc = stamp }, ct)
            );
        }

        return id;
    }
}
