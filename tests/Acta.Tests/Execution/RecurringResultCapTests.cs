using System.Collections.Immutable;
using Acta;
using Acta.Runtime.Modules.Execution.Workers;
using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// The cap is the only thing bounding a recurring slot's result history, because retention deletes
/// terminal jobs and a live recurring slot is never terminal. It is refused at startup, by name, rather
/// than on the completion request, which is built after the handler has run.
/// </summary>
public sealed class RecurringResultCapTests
{
    [Fact(DisplayName = "A definition that declares no cap keeps one result, not an unbounded history")]
    public void Attribute_default_is_one() => Assert.Equal(1, new JobAttribute("any").RecurringResultCap);

    [Fact(DisplayName = "A scheduled definition with a cap below one is refused at startup and named")]
    public void Scheduled_definition_without_a_cap_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorkerRuntimeInitializer.ValidateRecurringResultCaps([Descriptor("nightly", scheduled: true, cap: 0)])
        );
        Assert.Contains("'nightly'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("RecurringResultCap = 0", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A scheduled definition keeping at least one result starts")]
    public void Scheduled_definition_with_a_cap_starts() =>
        WorkerRuntimeInitializer.ValidateRecurringResultCaps([Descriptor("nightly", scheduled: true, cap: 1)]);

    [Fact(DisplayName = "An unscheduled definition carries no cap, because the branch reading it is unreachable")]
    public void Unscheduled_definition_ignores_the_cap() =>
        WorkerRuntimeInitializer.ValidateRecurringResultCaps([Descriptor("one-shot", scheduled: false, cap: 0)]);

    // The validator reads the name, the schedules and the cap; every other field is defaulted because it
    // never reaches them, and building a real record keeps the fact honest about what a descriptor is.
    private static JobDescriptor Descriptor(string jobName, bool scheduled, int cap) =>
        new(
            jobName,
            typeof(RecurringResultCapTests),
            MethodName: "Run",
            InputType: typeof(object),
            OutputType: null,
            InputPayloadFormat: default,
            OutputPayloadFormat: null,
            InvocationKind: default,
            RequiresJobContextParameter: false,
            RequiresCancellationToken: false,
            Priority: default,
            MaxAttempts: 1,
            AuditLevel: default,
            AlertProfile: default,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeOutput: null
        )
        {
            Schedules = scheduled
                ? [new ScheduleDescriptor(jobName, "default", "0 0 * * *", null, default, default, null, ImmutableArray<string>.Empty)]
                : [],
            RecurringResultCap = cap,
        };
}
