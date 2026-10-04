using Acta.Runtime;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// Boundary validation in <see cref="SetJobDefinitionOverridesHandler.Normalize"/> for the override
/// values that would otherwise fail later and worse: an execution timeout past CancelAfter's
/// ceiling crashes the attempt after the claim, a malformed rate limit stops metering silently, a
/// non-ASCII or over-length runbook URL fails or mangles per provider, and free-form display text
/// truncates to its column instead of surfacing a provider write error.
/// </summary>
public sealed class DefinitionOverrideValidationTests
{
    private static DefinitionPolicyOverrides Normalize(DefinitionPolicyOverrides overrides) =>
        SetJobDefinitionOverridesHandler.Normalize(overrides);

    [Fact]
    public void An_execution_timeout_past_the_CancelAfter_ceiling_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Normalize(new DefinitionPolicyOverrides(ExecutionTimeoutSeconds: JobDefinitionRegistration.MaxExecutionTimeoutSeconds + 1))
        );

        // The ceiling itself is a legal value.
        var normalized = Normalize(
            new DefinitionPolicyOverrides(ExecutionTimeoutSeconds: JobDefinitionRegistration.MaxExecutionTimeoutSeconds)
        );
        Assert.Equal(JobDefinitionRegistration.MaxExecutionTimeoutSeconds, normalized.ExecutionTimeoutSeconds);
    }

    [Theory]
    [InlineData("10")]
    [InlineData("10/d")]
    [InlineData("0/s")]
    [InlineData("1001/s")]
    public void A_rate_limit_override_that_is_not_a_rate_is_rejected(string rateLimit)
    {
        // Rejected rather than coerced, like Backoff: a meter that silently stops metering is worse
        // than a refused write.
        Assert.Throws<ArgumentException>(() => Normalize(new DefinitionPolicyOverrides(RateLimit: rateLimit)));

        Assert.Equal("1000/s", Normalize(new DefinitionPolicyOverrides(RateLimit: "1000/s")).RateLimit);
    }

    [Theory]
    [InlineData("https://rb/é")] // non-ASCII in an ASCII column
    [InlineData("https://rb/\U0001F600")] // astral characters are equally out of range
    public void A_non_ascii_runbook_url_is_rejected(string url) =>
        Assert.Throws<ArgumentException>(() => Normalize(new DefinitionPolicyOverrides(RunbookUrl: url)));

    [Fact]
    public void An_over_length_runbook_url_is_rejected_not_cut()
    {
        var url = "https://rb/" + new string('a', 600);
        Assert.Throws<ArgumentException>(() => Normalize(new DefinitionPolicyOverrides(RunbookUrl: url)));
    }

    [Fact]
    public void Display_text_overrides_truncate_to_their_columns()
    {
        var normalized = Normalize(new DefinitionPolicyOverrides(DisplayName: new string('d', 200), Description: new string('x', 600)));

        Assert.Equal(128, normalized.DisplayName!.Length);
        Assert.Equal(512, normalized.Description!.Length);
    }
}
