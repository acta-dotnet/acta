using Xunit;

namespace Acta.Tests.Execution;

/// <summary>The wait a claimed job carries: its claim instant minus its due instant, clamped to what the int column holds.</summary>
public sealed class ClaimedJobWaitTests
{
    private static readonly DateTime Due = new(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_claim_ahead_of_its_due_instant_stores_zero()
    {
        Assert.Equal(0, ClaimedJob.WaitMsBetween(Due, Due.AddTicks(-4000)));
        Assert.Equal(0, ClaimedJob.WaitMsBetween(Due, Due.AddMinutes(-5)));
    }

    [Fact]
    public void A_wait_past_the_columns_range_stores_the_cap()
    {
        Assert.Equal(int.MaxValue - 1, ClaimedJob.WaitMsBetween(Due, Due.AddMilliseconds(int.MaxValue - 1)));
        Assert.Equal(int.MaxValue, ClaimedJob.WaitMsBetween(Due, Due.AddMilliseconds(int.MaxValue)));
        Assert.Equal(int.MaxValue, ClaimedJob.WaitMsBetween(Due, Due.AddDays(30)));
    }

    [Fact]
    public void A_wait_in_range_is_its_whole_milliseconds()
    {
        Assert.Equal(0, ClaimedJob.WaitMsBetween(Due, Due));
        Assert.Equal(1234, ClaimedJob.WaitMsBetween(Due, Due.AddTicks(12_349_999)));
    }

    [Fact]
    public void A_claim_reads_its_wait_from_the_rows_due_and_claim_instants()
    {
        Assert.Equal(90_000, ClaimedJob.From(Row(Due.AddSeconds(90))).WaitMs);
        Assert.Null(ClaimedJob.From(Row(null)).WaitMs);
    }

    private static ClaimJobsRow Row(DateTime? claimedAtUtc) =>
        new(1, 2, 3, null, JobKindCode.Ordinary, null, null, 0, null, Due, Due, 0, Guid.Empty, null, claimedAtUtc, null, null, null, null);
}
