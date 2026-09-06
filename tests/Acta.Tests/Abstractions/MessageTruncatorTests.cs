using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Alerting;
using Xunit;

namespace Acta.Tests.Abstractions;

/// <summary>
/// MessageTruncator invariant: text leaving Truncate stores identically on every provider. No
/// unpaired surrogate may survive - Npgsql's UTF-8 encoder throws on one, SqlClient stores it,
/// SQLite replaces it - and a cut at the cap must never manufacture one out of a valid pair.
/// </summary>
public sealed class MessageTruncatorTests
{
    [Fact]
    public void Cut_at_a_surrogate_pair_boundary_backs_off_instead_of_splitting()
    {
        // 511 chars + a 2-char emoji: a raw cut at 512 would end on the lone high surrogate that
        // wedged the Postgres write.
        var value = new string('a', 511) + "\U0001F4A5";

        var truncated = value.Truncate(512);

        Assert.Equal(511, truncated!.Length);
        Assert.False(char.IsSurrogate(truncated[^1]));
    }

    [Fact]
    public void Intact_pairs_inside_the_cap_survive_untouched()
    {
        var value = "boom \U0001F4A5 done";

        Assert.Equal(value, value.Truncate(512));
    }

    [Fact]
    public void Unpaired_surrogates_are_replaced_wherever_they_sit()
    {
        // A lone high, a lone low, and a valid pair: only the strays become U+FFFD.
        var value = "a\uD83Db\uDCA5c\U0001F4A5d";

        var sanitized = value.Truncate(512);

        Assert.Equal("a�b�c\U0001F4A5d", sanitized);
    }

    [Fact]
    public void Nul_is_replaced_because_postgres_rejects_it_in_text()
    {
        Assert.Equal("a�b", "a\0b".Truncate(512));
    }

    [Fact]
    public void An_over_cap_value_yields_the_same_prefix_whether_or_not_the_tail_is_sanitized()
    {
        // The pair sits far past the cap: the sanitize pass must not need to reach it to cut correctly,
        // and a lone surrogate exactly at the cap boundary must still be seen.
        var farTail = new string('a', 100_000) + "\U0001F4A5";
        Assert.Equal(new string('a', 512), farTail.Truncate(512));

        var pairAcrossTheCut = new string('a', 512) + "\U0001F4A5";
        Assert.Equal(new string('a', 512), pairAcrossTheCut.Truncate(512));
    }

    [Fact]
    public void Plain_text_passes_through_as_the_same_instance()
    {
        var value = "no surrogates here";

        Assert.Same(value, value.Truncate(512));
    }

    [Fact]
    public void Null_value_and_null_cap_keep_their_contract()
    {
        Assert.Null(((string?)null).Truncate(512));
        Assert.Equal("abc", "abc".Truncate(null));
    }

    [Fact]
    public void Over_cap_ascii_still_cuts_at_the_cap()
    {
        var value = new string('x', 600);

        Assert.Equal(512, value.Truncate(512)!.Length);
    }

    [Fact]
    public void Rendered_alert_content_carries_no_unpaired_surrogate()
    {
        // The alert projector's real path: a stored reason within its own cap grows past the alert
        // cap once Render prefixes it, and the truncation must stay pair-safe there too.
        var reason = new string('r', 500) + "\U0001F4A5";
        var command = RaiseJobAlertCommand.Create(
            "test-ns",
            jobId: 1,
            AlertOriginCode.Automatic,
            AlertSeverityCode.Error,
            AlertKindCode.FinalFailure,
            title: "Job 'x' failed",
            message: $"Terminal failure: {reason}.",
            channelName: "default",
            AlertDeliveryStatusCode.Pending,
            deduplicationKey: null,
            sourceEventId: 1
        );

        AssertWellFormedUtf16(command.Message);
        Assert.True(command.Message.Length <= ActaTextLimits.AlertMessage);
    }

    private static void AssertWellFormedUtf16(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                Assert.True(i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]), $"lone high surrogate at {i}");
                i++;
                continue;
            }
            Assert.False(char.IsLowSurrogate(value[i]), $"lone low surrogate at {i}");
        }
    }
}
