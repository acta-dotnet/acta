using Xunit;

namespace Acta.Tests.Alerts;

public sealed class AlertFloorTests
{
    private static readonly DateTime Stamp = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Floor_compares_timestamps_before_ids_and_keeps_the_oldest_skip_above_it()
    {
        var skips = new PoisonAlertEvent[]
        {
            new(1, 101, Stamp.AddSeconds(1).Ticks),
            new(900, 102, Stamp.Ticks),
            new(800, 103, Stamp.Ticks),
        };
        var response = new GetAlertFloorResponse(true, new AlertsCursor(Stamp.AddSeconds(2).Ticks, 2), skips);

        Assert.Equal((Stamp, 799L), response.Floor(DateTime.UtcNow));
    }

    [Theory]
    [InlineData(-1, 900)]
    [InlineData(0, 5)]
    public void A_cursor_before_the_oldest_skip_stays_the_floor(int seconds, long id)
    {
        var cursor = new AlertsCursor(Stamp.AddSeconds(seconds).Ticks, id);
        var response = new GetAlertFloorResponse(true, cursor, [new PoisonAlertEvent(10, 101, Stamp.Ticks)]);

        Assert.Equal((cursor.Utc, id), response.Floor(DateTime.UtcNow));
    }

    [Fact]
    public void A_namespace_without_a_slot_uses_the_cutoff_and_maximum_id()
    {
        var response = new GetAlertFloorResponse(false, new AlertsCursor(0, 0), []);

        Assert.Equal((Stamp, long.MaxValue), response.Floor(Stamp));
    }

    [Fact]
    public void Skip_record_round_trips_the_event_position_and_job()
    {
        var skip = new PoisonAlertEvent(42, 123, Stamp.Ticks);

        Assert.Equal(skip, PoisonAlertEvent.Parse(skip.VariableName, skip.Record("ns", "invalid-event", "ArgumentException", "staged")));
        Assert.Null(PoisonAlertEvent.Parse("other-42", skip.Record("ns", "invalid-event", "ArgumentException", "staged")));
        Assert.Null(PoisonAlertEvent.Parse(skip.VariableName, "unreadable"));
    }
}
