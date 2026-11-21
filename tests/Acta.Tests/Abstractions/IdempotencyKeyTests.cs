using Xunit;

namespace Acta.Tests.Abstractions;

public sealed class IdempotencyKeyTests
{
    [Fact]
    public void ForDefinition_composes_the_exact_definition_qualified_key()
    {
        Assert.Equal("process-order:order-123", IdempotencyKey.ForDefinition("process-order", "order-123"));
    }

    [Fact]
    public void AcrossDefinitions_returns_the_exact_cross_definition_key()
    {
        Assert.Equal("migration-42", IdempotencyKey.AcrossDefinitions("migration-42"));
    }

    [Fact]
    public void ForTenant_composes_a_folded_tenant_with_the_exact_business_key()
    {
        Assert.Equal("acme:Invoice-123", IdempotencyKey.ForTenant(" Acme ", " Invoice-123 "));
    }

    [Fact]
    public void ForTenant_nests_as_the_business_key_of_ForDefinition()
    {
        Assert.Equal(
            "send-invoice:acme:invoice-123",
            IdempotencyKey.ForDefinition("send-invoice", IdempotencyKey.ForTenant("acme", "invoice-123"))
        );
    }

    [Theory]
    [InlineData("sys", "invoice-123")]
    [InlineData("sys.acme", "invoice-123")]
    [InlineData("bad tenant", "invoice-123")]
    [InlineData("acme", "bad key")]
    public void ForTenant_rejects_reserved_or_invalid_components(string tenantName, string businessKey)
    {
        Assert.Throws<ArgumentException>(() => IdempotencyKey.ForTenant(tenantName, businessKey));
    }

    [Fact]
    public void ForTenant_rejects_a_length_over_128_characters()
    {
        Assert.Throws<ArgumentException>(() => IdempotencyKey.ForTenant("acme", new string('a', IdempotencyKey.MaxLength)));
    }

    [Fact]
    public void Components_are_trimmed_the_name_folded_and_the_key_kept_exact()
    {
        Assert.Equal("process-order:Order-123", IdempotencyKey.ForDefinition(" Process-Order ", " Order-123 "));
        Assert.Equal("Migration-42", IdempotencyKey.AcrossDefinitions(" Migration-42 "));
        Assert.Equal("send:auth0|123", IdempotencyKey.ForDefinition("send", "auth0|123"));
    }

    [Theory]
    [InlineData("bad definition", "order-123")]
    [InlineData("process-order", "bad key")]
    [InlineData("process-order", "café")]
    public void Invalid_characters_are_rejected(string definitionName, string businessKey)
    {
        Assert.Throws<ArgumentException>(() => IdempotencyKey.ForDefinition(definitionName, businessKey));
    }

    [Fact]
    public void Final_user_key_rejects_the_reserved_system_prefix_in_any_case()
    {
        Assert.Throws<ArgumentException>(() => IdempotencyKey.AcrossDefinitions("sys.customer-42"));
        Assert.Throws<ArgumentException>(() => IdempotencyKey.AcrossDefinitions("SYS.customer-42"));
        Assert.Throws<ArgumentException>(() => IdempotencyKey.ForDefinition("Sys.Retention", "customer-42"));
    }

    [Fact]
    public void System_prefix_is_allowed_inside_a_definition_qualified_component()
    {
        Assert.Equal("send-order:sys.customer-42", IdempotencyKey.ForDefinition("send-order", "sys.customer-42"));
    }

    [Fact]
    public void Final_composed_key_accepts_the_128_character_boundary()
    {
        var businessKey = new string('a', IdempotencyKey.MaxLength - 2);

        var key = IdempotencyKey.ForDefinition("a", businessKey);

        Assert.Equal(IdempotencyKey.MaxLength, key.Length);
    }

    [Fact]
    public void Final_composed_key_rejects_a_length_over_128_characters()
    {
        var businessKey = new string('a', IdempotencyKey.MaxLength - 1);

        Assert.Throws<ArgumentException>(() => IdempotencyKey.ForDefinition("a", businessKey));
    }

    [Fact]
    public void PerTimeBucket_has_the_exact_canonical_shape()
    {
        var key = IdempotencyKey.PerTimeBucket("send-order", "order-123", DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(15));

        Assert.Equal("send-order:order-123:bucket:15m:0", key);
    }

    [Fact]
    public void Instants_in_the_same_bucket_produce_the_same_key()
    {
        var first = DateTimeOffset.UnixEpoch.AddMinutes(30).AddSeconds(1);
        var second = DateTimeOffset.UnixEpoch.AddMinutes(44).AddSeconds(59);

        Assert.Equal(
            IdempotencyKey.PerTimeBucket("send-order", "order-123", first, TimeSpan.FromMinutes(15)),
            IdempotencyKey.PerTimeBucket("send-order", "order-123", second, TimeSpan.FromMinutes(15))
        );
    }

    public static TheoryData<TimeSpan> BucketPrecisions =>
        [TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromTicks(1)];

    [Theory]
    [MemberData(nameof(BucketPrecisions))]
    public void Adjacent_buckets_produce_different_keys(TimeSpan bucketSize)
    {
        var first = IdempotencyKey.PerTimeBucket("send-order", "order-123", DateTimeOffset.UnixEpoch, bucketSize);
        var second = IdempotencyKey.PerTimeBucket("send-order", "order-123", DateTimeOffset.UnixEpoch.Add(bucketSize), bucketSize);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Equivalent_instants_with_different_offsets_converge()
    {
        var local = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.FromHours(2));
        var utc = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            IdempotencyKey.PerTimeBucket("send-order", "order-123", local, TimeSpan.FromMinutes(15)),
            IdempotencyKey.PerTimeBucket("send-order", "order-123", utc, TimeSpan.FromMinutes(15))
        );
    }

    [Fact]
    public void Pre_epoch_partial_bucket_uses_floor_division()
    {
        var key = IdempotencyKey.PerTimeBucket("send-order", "order-123", DateTimeOffset.UnixEpoch.AddTicks(-1), TimeSpan.FromSeconds(1));

        Assert.Equal("send-order:order-123:bucket:1s:-1", key);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Zero_and_negative_bucket_sizes_throw(long ticks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IdempotencyKey.PerTimeBucket("send-order", "order-123", DateTimeOffset.UnixEpoch, TimeSpan.FromTicks(ticks))
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
        var key = IdempotencyKey.PerTimeBucket("send-order", "order-123", DateTimeOffset.UnixEpoch, bucketSize);

        Assert.Equal($"send-order:order-123:bucket:{canonicalSize}:0", key);
    }

    [Fact]
    public void PerHour_equals_a_one_hour_time_bucket()
    {
        var instant = new DateTimeOffset(2026, 7, 15, 10, 42, 0, TimeSpan.FromHours(2));

        Assert.Equal(
            IdempotencyKey.PerTimeBucket("send-order", "order-123", instant, TimeSpan.FromHours(1)),
            IdempotencyKey.PerHour("send-order", "order-123", instant)
        );
    }

    [Fact]
    public void PerDay_equals_a_one_day_time_bucket()
    {
        var day = new DateOnly(2026, 7, 15);
        var midnightUtc = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        Assert.Equal(
            IdempotencyKey.PerTimeBucket("send-order", "order-123", midnightUtc, TimeSpan.FromDays(1)),
            IdempotencyKey.PerDay("send-order", "order-123", day)
        );
    }
}
