using System.Reflection;
using System.Text.RegularExpressions;
using Acta.Tests.Architecture;
using Xunit;

namespace Acta.Tests.Contracts;

/// <summary>
/// A reason is stored inside an event's details, so its spelling is part of the ledger: a query or an alert rule that
/// reads <c>lease-expired</c> must keep finding it. Pins every reason's string, where C# names it and where a
/// statement writes it.
/// </summary>
public sealed partial class EventReasonContractTests
{
    // One line per member of EventReason, in declaration order, as stored.
    private const string Frozen = """
        UnhandledException=unhandled-exception
        LeaseExpired=lease-expired
        ExecutionTimeout=execution-timeout
        NonRetryableException=non-retryable-exception
        DeadlineExceeded=deadline-exceeded
        AttemptAborted=attempt-aborted
        ParentCancelled=parent-cancelled
        DefinitionRetired=definition-retired
        HandlerRescheduled=handler-rescheduled
        HandlerSuspended=handler-suspended
        HandlerFailed=handler-failed
        HandlerCancelled=handler-cancelled
        HandlerPaused=handler-paused
        SignalReleased=signal-released
        ConcurrencyKeyHeld=concurrency-key-held
        StepInterrupted=step-interrupted
        ResultOversized=result-oversized
        WaitTimedOut=wait-timed-out
        RateLimited=rate-limited
        LaneRepaired=lane-repaired
        """;

    // What a schedule event says of a decided range or a pause. Written where the event is written, with no constant.
    private static readonly string[] ScheduleReasons = ["capacity", "expired", "firing-failed", "retimed", "source-changed"];

    [Fact(DisplayName = "Every job event reason keeps the string the ledger stores")]
    public void Every_reason_keeps_its_stored_string()
    {
        var actual = string.Join("\n", Reasons().Select(reason => $"{reason.Name}={reason.Value}"));

        Assert.Equal(Frozen.ReplaceLineEndings("\n"), actual);
    }

    [Fact(DisplayName = "Every reason a statement or a handler writes as a literal is one of the pinned reasons")]
    public void Every_written_reason_is_pinned()
    {
        var known = Reasons().Select(reason => reason.Value).Concat(ScheduleReasons).ToHashSet(StringComparer.Ordinal);
        var written = ShippedCode
            .SourceFiles("*.sql")
            .Where(file => !file.Relative.Contains("/Migrations/", StringComparison.Ordinal))
            .SelectMany(file =>
                SqlReason().Matches(File.ReadAllText(file.Path)).Select(match => (file.Relative, Reason: match.Groups[1].Value))
            )
            .Concat(
                ShippedCode
                    .SourceFiles("*.cs")
                    .SelectMany(file =>
                    {
                        var text = File.ReadAllText(file.Path);
                        return CSharpReason()
                            .Matches(text)
                            .Concat(RangeReason().Matches(text))
                            .Select(match => (file.Relative, Reason: match.Groups[1].Value));
                    })
            )
            .ToList();

        // The scan must be reading something in each form, or a changed spelling of the details would pass unseen. It
        // reads literals: a reason that reaches a builder through a variable is a constant of EventReason, pinned above.
        Assert.Contains(written, entry => entry.Reason == EventReason.LeaseExpired);
        Assert.Contains(written, entry => entry.Reason == "firing-failed");
        Assert.Contains(written, entry => entry.Reason == "retimed");
        Assert.Contains(written, entry => entry.Reason == "capacity");
        Assert.Contains(written, entry => entry.Reason == "source-changed");
        Assert.Empty(written.Where(entry => !known.Contains(entry.Reason)).Select(entry => $"{entry.Relative}: {entry.Reason}").Distinct());
    }

    private static IEnumerable<(string Name, string Value)> Reasons() =>
        typeof(EventReason)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .OrderBy(field => field.MetadataToken)
            .Select(field => (field.Name, (string)field.GetRawConstantValue()!));

    // "reason":"lease-expired" inside a JSON text a statement builds.
    [GeneratedRegex(
        """
            "reason":"([a-z][a-z-]*)"
            """,
        RegexOptions.IgnorePatternWhitespace
    )]
    private static partial Regex SqlReason();

    // ("reason", "retimed") in a details builder.
    [GeneratedRegex(
        """
            \("reason",\s*"([a-z][a-z-]*)"\)
            """,
        RegexOptions.IgnorePatternWhitespace
    )]
    private static partial Regex CSharpReason();

    // RangeDetails(..., "capacity") and RangeDetails(..., changed ? "source-changed" : null): the admitter's range builder
    // takes the reason as its last argument.
    [GeneratedRegex(
        """
            RangeDetails\((?:[^()]|\([^()]*\))*?,\s*(?:[^,()"]*\?\s*)?"([a-z][a-z-]*)"(?:\s*:\s*null)?\s*\)
            """,
        RegexOptions.IgnorePatternWhitespace
    )]
    private static partial Regex RangeReason();
}
