using Acta.Runtime.Modules.Execution;
using Acta.Testing.Hosting;
using Xunit;

namespace Acta.Tests.Testing;

/// <summary>
/// <see cref="ActaTestHost"/> maps the runtime's tick outcome to the public
/// <see cref="ActaRunOutcome"/> with a bare numeric cast, which is only sound while the two enums
/// agree member for member. Nothing else holds them together: the public enum is a frozen surface
/// and the internal one is free to move, so this pin is what turns a silent remap into a red test.
/// </summary>
public sealed class ActaRunOutcomeParityTests
{
    [Fact]
    public void Public_run_outcome_members_mirror_the_internal_tick_outcome()
    {
        foreach (var name in Enum.GetNames<ActaRunOutcome>())
        {
            Assert.True(Enum.TryParse<RunOnceOutcome>(name, out var internalValue), $"RunOnceOutcome lacks member '{name}'.");
            Assert.Equal((byte)Enum.Parse<ActaRunOutcome>(name), (byte)internalValue);
        }
    }
}
