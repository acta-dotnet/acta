using Acta;
using Acta.Runtime.Modules.Execution;
using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// The cap is the only thing bounding a recurring slot's result history, because retention deletes
/// terminal jobs and a live recurring slot is never terminal.
/// </summary>
public sealed class RecurringResultCapTests
{
    [Fact(DisplayName = "A definition that declares no cap keeps one result, not an unbounded history")]
    public void Attribute_default_is_one()
    {
        Assert.Equal(1, new JobAttribute("any").RecurringResultCap);
    }

    [Fact(DisplayName = "A recurring completion carrying no cap is refused instead of keeping every result row")]
    public void Recurring_completion_without_a_cap_is_refused()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => RequestWith(cap: 0, recurring: true));
        Assert.Contains("recurring fire", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A non-recurring completion carries no cap, because the branch reading it is unreachable")]
    public void Non_recurring_completion_leaves_the_cap_at_zero()
    {
        Assert.Equal(0, RequestWith(cap: 0, recurring: false).RecurringResultCap);
    }

    [Fact(DisplayName = "A recurring completion carrying a cap keeps it")]
    public void Recurring_completion_keeps_its_cap()
    {
        Assert.Equal(3, RequestWith(cap: 3, recurring: true).RecurringResultCap);
    }

    private static CompleteExecutionRequest RequestWith(int cap, bool recurring) =>
        new(
            JobId: 1,
            WorkerId: 1,
            ExpectedExecutionNumber: 1,
            Outcome: ExecutionOutcome.Succeeded,
            ResultFormatId: 0,
            Result: ReadOnlyMemory<byte>.Empty,
            ScheduleAdvances: recurring ? [new ScheduleAdvance(1, DateTime.UtcNow, null)] : null,
            RecurringResultCap: cap
        );
}
