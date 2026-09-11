using Anvil.Bench;
using Xunit;

namespace Acta.Tests.Benchmarks;

public sealed class PageLatchIndexDeltaTests
{
    [Fact]
    public void Drops_an_index_whose_counters_went_backwards()
    {
        var before = new Dictionary<string, PageLatchIndexStat>
        {
            ["jobs.pk_jobs"] = new(WaitMs: 500, Waits: 50),
            ["jobs.ix_jobs_status"] = new(WaitMs: 100, Waits: 10),
        };
        var after = new Dictionary<string, PageLatchIndexStat>
        {
            // Rebuilt mid-cell: counters reset below the "before" sample.
            ["jobs.pk_jobs"] = new(WaitMs: 20, Waits: 2),
            ["jobs.ix_jobs_status"] = new(WaitMs: 260, Waits: 26),
        };

        var delta = PageLatchIndexDelta.Compute(before, after);

        Assert.NotNull(delta);
        var single = Assert.Single(delta!);
        Assert.Equal("jobs.ix_jobs_status", single.Key);
        Assert.Equal(new PageLatchIndexStat(160, 16), single.Value);
    }

    [Fact]
    public void Keeps_only_the_five_busiest_nonzero_entries()
    {
        var before = new Dictionary<string, PageLatchIndexStat>();
        var after = Enumerable
            .Range(0, 7)
            .ToDictionary(i => $"jobs.ix_{i}", i => new PageLatchIndexStat(WaitMs: (i + 1) * 10, Waits: i + 1));
        after["jobs.ix_untouched"] = new PageLatchIndexStat(WaitMs: 0, Waits: 0);

        var delta = PageLatchIndexDelta.Compute(before, after);

        Assert.NotNull(delta);
        Assert.Equal(5, delta!.Count);
        Assert.DoesNotContain("jobs.ix_untouched", delta.Keys);
        Assert.Equal(
            ["jobs.ix_6", "jobs.ix_5", "jobs.ix_4", "jobs.ix_3", "jobs.ix_2"],
            delta.OrderByDescending(kv => kv.Value.WaitMs).Select(kv => kv.Key)
        );
    }

    [Fact]
    public void Treats_a_new_index_absent_from_the_before_sample_as_starting_at_zero()
    {
        var before = new Dictionary<string, PageLatchIndexStat>();
        var after = new Dictionary<string, PageLatchIndexStat> { ["jobs.ix_new"] = new(WaitMs: 42, Waits: 4) };

        var delta = PageLatchIndexDelta.Compute(before, after);

        Assert.NotNull(delta);
        Assert.Equal(new PageLatchIndexStat(42, 4), delta!["jobs.ix_new"]);
    }

    [Fact]
    public void Returns_null_when_the_after_sample_is_missing()
    {
        Assert.Null(PageLatchIndexDelta.Compute(new Dictionary<string, PageLatchIndexStat>(), null));
    }
}
