using Acta.Runtime.Modules.Outbox;
using Xunit;

namespace Acta.Tests.Outbox;

/// <summary>
/// The sources read's tick-summary token parse: the cross-peer contract is the rendered
/// <see cref="OutboxTickSummary"/> string, so the parse must read exactly what ToString renders and
/// degrade to "unknown" (null) rather than zero on anything else.
/// </summary>
public sealed class OutboxApiTests
{
    [Fact]
    public void Parses_backlog_and_quarantine_from_a_rendered_summary()
    {
        var tick = new OutboxTickSummary(512, 500, 12, 0, 46032, 7).ToString();

        Assert.Equal(46032, OutboxApi.ParseToken(tick, "backlog="));
        Assert.Equal(7, OutboxApi.ParseToken(tick, "quarantine="));
    }

    [Fact]
    public void Conditional_trailing_tokens_do_not_spoil_the_counters_before_them()
    {
        var tick = new OutboxTickSummary(2, 1, 0, 0, 5, 3, Requeued: 3, Discarded: 1).ToString();

        Assert.Equal(5, OutboxApi.ParseToken(tick, "backlog="));
        Assert.Equal(3, OutboxApi.ParseToken(tick, "quarantine="));
    }

    [Fact]
    public void Missing_token_or_summary_reads_as_unknown_not_zero()
    {
        Assert.Null(OutboxApi.ParseToken(null, "backlog="));
        Assert.Null(OutboxApi.ParseToken("claimed=1 relayed=1", "backlog="));
        Assert.Null(OutboxApi.ParseToken("backlog=oops quarantine=2", "backlog="));
    }

    [Fact]
    public void Quarantine_total_is_read_beside_the_similarly_named_per_tick_counter()
    {
        // The summary carries both quarantined= (rows this tick) and quarantine= (current total);
        // the parse must answer from the total, not the per-tick neighbor.
        var tick = "claimed=9 relayed=9 dedup=0 quarantined=4 backlog=1 quarantine=6";

        Assert.Equal(6, OutboxApi.ParseToken(tick, "quarantine="));
        Assert.Equal(4, OutboxApi.ParseToken(tick, "quarantined="));
    }
}
