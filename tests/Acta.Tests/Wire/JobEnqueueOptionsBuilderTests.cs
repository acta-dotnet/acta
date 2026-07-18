using Xunit;

namespace Acta.Tests.Wire;

/// <summary>
/// JobEnqueueOptionsBuilder contract: eager validation, last-write-wins tag dedupe, repeatable
/// snapshot Build, and parity with the JobEnqueueOptions object shape.
/// </summary>
public sealed class JobEnqueueOptionsBuilderTests
{
    [Fact]
    public void Build_snapshots_all_setters()
    {
        var when = DateTimeOffset.UtcNow.AddHours(1);

        var options = new JobEnqueueOptionsBuilder()
            .Namespace("payments")
            .DeduplicationKey("invoice-1")
            .CorrelationKey("corr-1")
            .ExclusiveKey("acct-7")
            .Priority(JobPriorityCode.High)
            .NextExecutionAt(when)
            .Tag("team", "payments")
            .Build();

        Assert.Equal("payments", options.Namespace);
        Assert.Equal("invoice-1", options.DeduplicationKey);
        Assert.Equal("corr-1", options.CorrelationKey);
        Assert.Equal("acct-7", options.ExclusiveKey);
        Assert.Equal(JobPriorityCode.High, options.Priority);
        Assert.Equal(when.UtcDateTime, options.NextRunAtUtc);
        var tag = Assert.Single(options.Tags!);
        Assert.Equal("team", tag.Name);
        Assert.Equal("payments", tag.Value);
    }

    [Fact]
    public void Build_with_no_setters_yields_all_null_options()
    {
        var options = new JobEnqueueOptionsBuilder().Build();

        Assert.Null(options.Namespace);
        Assert.Null(options.DeduplicationKey);
        Assert.Null(options.Tags);
        Assert.Null(options.NextRunAtUtc);
    }

    [Fact]
    public void Tag_dedupe_is_last_write_wins()
    {
        var options = new JobEnqueueOptionsBuilder().Tag("env", "a").Tag("env", "b").Build();

        var tag = Assert.Single(options.Tags!);
        Assert.Equal("b", tag.Value);
    }

    [Fact]
    public void Build_is_repeatable_and_isolated()
    {
        var builder = new JobEnqueueOptionsBuilder().DeduplicationKey("first");
        var first = builder.Build();
        builder.DeduplicationKey("second");

        Assert.Equal("first", first.DeduplicationKey);
        Assert.Equal("second", builder.Build().DeduplicationKey);
    }

    [Fact]
    public void Delayed_rejects_negative_delay()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobEnqueueOptionsBuilder().Delayed(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Delayed_sets_relative_delay_seconds_and_clears_absolute_instant()
    {
        var options = new JobEnqueueOptionsBuilder()
            .NextExecutionAt(DateTimeOffset.UtcNow.AddHours(1))
            .Delayed(TimeSpan.FromMinutes(2))
            .Build();

        Assert.Equal(120, options.DelaySeconds);
        Assert.Null(options.NextRunAtUtc);
    }

    [Fact]
    public void DeduplicationKey_rejects_system_prefix_and_whitespace()
    {
        Assert.ThrowsAny<ArgumentException>(() => new JobEnqueueOptionsBuilder().DeduplicationKey("sys.reserved"));
        Assert.ThrowsAny<ArgumentException>(() => new JobEnqueueOptionsBuilder().DeduplicationKey(" "));
    }

    [Fact]
    public void Namespace_rejects_non_kebab()
    {
        Assert.ThrowsAny<ArgumentException>(() => new JobEnqueueOptionsBuilder().Namespace("Not Kebab"));
    }
}
