using Acta.Runtime.Querying;
using Xunit;

namespace Acta.Tests.Api;

/// <summary>
/// Unit coverage for the opaque list-cursor codec and the page-size rules: round-trips per key
/// shape, and rejection of malformed, foreign, stale-order, stale-filter, and wrong-shape cursors.
/// </summary>
public sealed class PageCursorCodecTests
{
    private const string Op = "ListJobs";
    private const string Order = "created_at_utc desc, id desc";
    private const string FilterHash = "abc123";

    private static readonly DateTime Utc = new(2026, 6, 12, 6, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Round_trips_utc_and_long_keys()
    {
        var cursor = PageCursorCodec.Encode(Op, Order, FilterHash, [Utc, 12345L]);

        var keys = PageCursorCodec.Decode(cursor, Op, Order, FilterHash, [CursorKeyKind.Utc, CursorKeyKind.Long]);

        Assert.Equal(2, keys.Length);
        var utc = Assert.IsType<DateTime>(keys[0]);
        Assert.Equal(Utc, utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(12345L, Assert.IsType<long>(keys[1]));
    }

    [Fact]
    public void Round_trips_text_text_int_keys()
    {
        var cursor = PageCursorCodec.Encode("ListJobDefinitions", "ns asc, name asc, id asc", FilterHash, ["billing", "send-invoice", 42]);

        var keys = PageCursorCodec.Decode(
            cursor,
            "ListJobDefinitions",
            "ns asc, name asc, id asc",
            FilterHash,
            [CursorKeyKind.Text, CursorKeyKind.Text, CursorKeyKind.Int]
        );

        Assert.Equal("billing", Assert.IsType<string>(keys[0]));
        Assert.Equal("send-invoice", Assert.IsType<string>(keys[1]));
        Assert.Equal(42, Assert.IsType<int>(keys[2]));
    }

    [Fact]
    public void Round_trips_utc_and_int_keys()
    {
        var cursor = PageCursorCodec.Encode("ListWorkers", "last_seen desc, id desc", FilterHash, [Utc, 7]);

        var keys = PageCursorCodec.Decode(
            cursor,
            "ListWorkers",
            "last_seen desc, id desc",
            FilterHash,
            [CursorKeyKind.Utc, CursorKeyKind.Int]
        );

        Assert.Equal(7, Assert.IsType<int>(keys[1]));
    }

    [Fact]
    public void Rejects_oversized_cursor()
    {
        var oversized = new string('A', 5000);

        Assert.Throws<InvalidPageCursorException>(() =>
            PageCursorCodec.Decode(oversized, Op, Order, FilterHash, [CursorKeyKind.Utc, CursorKeyKind.Long])
        );
    }

    [Fact]
    public void Rejects_malformed_base64()
    {
        Assert.Throws<InvalidPageCursorException>(() =>
            PageCursorCodec.Decode("not base64url!!", Op, Order, FilterHash, [CursorKeyKind.Utc, CursorKeyKind.Long])
        );
    }

    [Fact]
    public void Rejects_non_json_payload()
    {
        var cursor = Convert.ToBase64String("hello"u8).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Throws<InvalidPageCursorException>(() =>
            PageCursorCodec.Decode(cursor, Op, Order, FilterHash, [CursorKeyKind.Utc, CursorKeyKind.Long])
        );
    }

    [Fact]
    public void Rejects_unsupported_version()
    {
        var json = """{"v":2,"op":"ListJobs","o":"created_at_utc desc, id desc","f":"abc123","k":["2026-06-12T06:30:00.0000000Z",1]}""";
        var cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Throws<InvalidPageCursorException>(() =>
            PageCursorCodec.Decode(cursor, Op, Order, FilterHash, [CursorKeyKind.Utc, CursorKeyKind.Long])
        );
    }

    [Fact]
    public void Rejects_operation_mismatch()
    {
        var cursor = PageCursorCodec.Encode(Op, Order, FilterHash, [Utc, 1L]);

        Assert.Throws<InvalidPageCursorException>(() =>
            PageCursorCodec.Decode(cursor, "ListJobAlerts", Order, FilterHash, [CursorKeyKind.Utc, CursorKeyKind.Long])
        );
    }

    [Fact]
    public void Rejects_order_mismatch()
    {
        var cursor = PageCursorCodec.Encode(Op, Order, FilterHash, [Utc, 1L]);

        Assert.Throws<InvalidPageCursorException>(() =>
            PageCursorCodec.Decode(cursor, Op, "id desc", FilterHash, [CursorKeyKind.Utc, CursorKeyKind.Long])
        );
    }

    [Fact]
    public void Rejects_filter_hash_mismatch()
    {
        var cursor = PageCursorCodec.Encode(Op, Order, FilterHash, [Utc, 1L]);

        Assert.Throws<InvalidPageCursorException>(() =>
            PageCursorCodec.Decode(cursor, Op, Order, "other", [CursorKeyKind.Utc, CursorKeyKind.Long])
        );
    }

    [Fact]
    public void Rejects_wrong_key_count()
    {
        var cursor = PageCursorCodec.Encode(Op, Order, FilterHash, [Utc]);

        Assert.Throws<InvalidPageCursorException>(() =>
            PageCursorCodec.Decode(cursor, Op, Order, FilterHash, [CursorKeyKind.Utc, CursorKeyKind.Long])
        );
    }

    [Fact]
    public void Rejects_wrong_key_kind()
    {
        var cursor = PageCursorCodec.Encode(Op, Order, FilterHash, ["text", 1L]);

        Assert.Throws<InvalidPageCursorException>(() =>
            PageCursorCodec.Decode(cursor, Op, Order, FilterHash, [CursorKeyKind.Utc, CursorKeyKind.Long])
        );
    }

    [Fact]
    public void Filter_hash_is_stable_and_skips_nulls()
    {
        var a = QueryFilterHash.Compute([("ns", "billing"), ("status", null), ("name", "send")]);
        var b = QueryFilterHash.Compute([("ns", "billing"), ("name", "send")]);
        var c = QueryFilterHash.Compute([("ns", "billing"), ("status", "30"), ("name", "send")]);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Theory]
    [InlineData(null, 50)]
    [InlineData(1, 1)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    public void Page_size_normalizes(int? requested, int expected)
    {
        Assert.Equal(expected, JobsQueryLimits.NormalizePageSize(requested));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Page_size_below_one_throws(int requested)
    {
        Assert.Throws<InvalidQueryException>(() => JobsQueryLimits.NormalizePageSize(requested));
    }
}
