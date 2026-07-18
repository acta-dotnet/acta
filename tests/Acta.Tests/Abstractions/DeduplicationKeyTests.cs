using Xunit;

namespace Acta.Tests.Abstractions;

public sealed class DeduplicationKeyTests
{
    [Fact]
    public void ForDefinition_composes_the_exact_definition_qualified_key()
    {
        Assert.Equal("process-order:order-123", DeduplicationKey.ForDefinition("process-order", "order-123"));
    }

    [Fact]
    public void AcrossDefinitions_returns_the_exact_cross_definition_key()
    {
        Assert.Equal("migration-42", DeduplicationKey.AcrossDefinitions("migration-42"));
    }

    [Fact]
    public void Components_are_trimmed_and_lowercase_normalized()
    {
        Assert.Equal("process-order:order-123", DeduplicationKey.ForDefinition(" Process-Order ", " Order-123 "));
        Assert.Equal("migration-42", DeduplicationKey.AcrossDefinitions(" Migration-42 "));
    }

    [Theory]
    [InlineData("bad definition", "order-123")]
    [InlineData("process-order", "bad key")]
    [InlineData("process-order", "café")]
    public void Invalid_characters_are_rejected(string definitionName, string businessKey)
    {
        Assert.Throws<ArgumentException>(() => DeduplicationKey.ForDefinition(definitionName, businessKey));
    }

    [Fact]
    public void Final_user_key_rejects_the_reserved_system_prefix()
    {
        Assert.Throws<ArgumentException>(() => DeduplicationKey.AcrossDefinitions("sys.customer-42"));
    }

    [Fact]
    public void System_prefix_is_allowed_inside_a_definition_qualified_component()
    {
        Assert.Equal("send-order:sys.customer-42", DeduplicationKey.ForDefinition("send-order", "sys.customer-42"));
    }

    [Fact]
    public void Final_composed_key_accepts_the_128_character_boundary()
    {
        var businessKey = new string('a', DeduplicationKey.MaxLength - 2);

        var key = DeduplicationKey.ForDefinition("a", businessKey);

        Assert.Equal(DeduplicationKey.MaxLength, key.Length);
    }

    [Fact]
    public void Final_composed_key_rejects_a_length_over_128_characters()
    {
        var businessKey = new string('a', DeduplicationKey.MaxLength - 1);

        Assert.Throws<ArgumentException>(() => DeduplicationKey.ForDefinition("a", businessKey));
    }

    [Fact]
    public void PerTimeBucket_has_the_exact_canonical_shape()
    {
        var key = DeduplicationKey.PerTimeBucket("send-order", "order-123", DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(15));

        Assert.Equal("send-order:order-123:bucket:15m:0", key);
    }

    [Fact]
    public void Instants_in_the_same_bucket_produce_the_same_key()
    {
        var first = DateTimeOffset.UnixEpoch.AddMinutes(30).AddSeconds(1);
        var second = DateTimeOffset.UnixEpoch.AddMinutes(44).AddSeconds(59);

        Assert.Equal(
            DeduplicationKey.PerTimeBucket("send-order", "order-123", first, TimeSpan.FromMinutes(15)),
            DeduplicationKey.PerTimeBucket("send-order", "order-123", second, TimeSpan.FromMinutes(15))
        );
    }

    public static TheoryData<TimeSpan> BucketPrecisions =>
        new() { TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromTicks(1) };

    [Theory]
    [MemberData(nameof(BucketPrecisions))]
    public void Adjacent_buckets_produce_different_keys(TimeSpan bucketSize)
    {
        var first = DeduplicationKey.PerTimeBucket("send-order", "order-123", DateTimeOffset.UnixEpoch, bucketSize);
        var second = DeduplicationKey.PerTimeBucket("send-order", "order-123", DateTimeOffset.UnixEpoch.Add(bucketSize), bucketSize);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Equivalent_instants_with_different_offsets_converge()
    {
        var local = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.FromHours(2));
        var utc = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            DeduplicationKey.PerTimeBucket("send-order", "order-123", local, TimeSpan.FromMinutes(15)),
            DeduplicationKey.PerTimeBucket("send-order", "order-123", utc, TimeSpan.FromMinutes(15))
        );
    }

    [Fact]
    public void Pre_epoch_partial_bucket_uses_floor_division()
    {
        var key = DeduplicationKey.PerTimeBucket("send-order", "order-123", DateTimeOffset.UnixEpoch.AddTicks(-1), TimeSpan.FromSeconds(1));

        Assert.Equal("send-order:order-123:bucket:1s:-1", key);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Zero_and_negative_bucket_sizes_throw(long ticks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DeduplicationKey.PerTimeBucket("send-order", "order-123", DateTimeOffset.UnixEpoch, TimeSpan.FromTicks(ticks))
        );
    }

    public static TheoryData<TimeSpan, string> CanonicalDurations =>
        new()
        {
            { TimeSpan.FromSeconds(60), "1m" },
            { TimeSpan.FromMinutes(60), "1h" },
            { TimeSpan.FromHours(24), "1d" },
            { TimeSpan.FromTicks(10_000), "1ms" },
        };

    [Theory]
    [MemberData(nameof(CanonicalDurations))]
    public void Equivalent_durations_use_the_largest_exact_canonical_unit(TimeSpan bucketSize, string canonicalSize)
    {
        var key = DeduplicationKey.PerTimeBucket("send-order", "order-123", DateTimeOffset.UnixEpoch, bucketSize);

        Assert.Equal($"send-order:order-123:bucket:{canonicalSize}:0", key);
    }

    [Fact]
    public void PerHour_equals_a_one_hour_time_bucket()
    {
        var instant = new DateTimeOffset(2026, 7, 15, 10, 42, 0, TimeSpan.FromHours(2));

        Assert.Equal(
            DeduplicationKey.PerTimeBucket("send-order", "order-123", instant, TimeSpan.FromHours(1)),
            DeduplicationKey.PerHour("send-order", "order-123", instant)
        );
    }

    [Fact]
    public void PerDay_equals_a_one_day_time_bucket()
    {
        var day = new DateOnly(2026, 7, 15);
        var midnightUtc = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        Assert.Equal(
            DeduplicationKey.PerTimeBucket("send-order", "order-123", midnightUtc, TimeSpan.FromDays(1)),
            DeduplicationKey.PerDay("send-order", "order-123", day)
        );
    }
}
