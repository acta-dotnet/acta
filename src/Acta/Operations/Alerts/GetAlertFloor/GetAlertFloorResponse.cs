using System.Globalization;
using System.Text;

namespace Acta;

/// <summary>
/// What the <c>sys.alerts</c> projector has consumed in one namespace: its cursor, and the poison events it skipped
/// and has not dispositioned. Without a slot no projector runs there, so nothing is owed and the floor is the
/// purge cutoff itself.
/// </summary>
internal sealed record GetAlertFloorResponse(bool SlotFound, AlertsCursor Cursor, IReadOnlyList<PoisonAlertEvent> Poison)
{
    /// <summary>
    /// The minimum of the cursor and each skip's predecessor position, so the strict above-floor predicate keeps
    /// the oldest skip itself. <paramref name="cutoffUtc"/> with the largest id when the namespace has no projector.
    /// </summary>
    public (DateTime Utc, long EventId) Floor(DateTime cutoffUtc)
    {
        if (!SlotFound)
        {
            return (cutoffUtc, long.MaxValue);
        }

        var floor = (Cursor.Ticks, Cursor.EventId);
        foreach (var skip in Poison)
        {
            var position = (skip.Ticks, EventId: skip.EventId - 1);
            if (position.CompareTo(floor) < 0)
            {
                floor = position;
            }
        }
        return (new DateTime(floor.Ticks, DateTimeKind.Utc), floor.EventId);
    }

    /// <summary>The skipped events as the JSON array the replay read consumes, or null when there are none.</summary>
    public string? PoisonJson()
    {
        if (Poison.Count == 0)
        {
            return null;
        }

        var text = new StringBuilder("[");
        for (var i = 0; i < Poison.Count; i++)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"{(i > 0 ? "," : "")}{{\"event_id\":{Poison[i].EventId},\"job_id\":{Poison[i].JobId}}}"
            );
        }
        return text.Append(']').ToString();
    }
}
