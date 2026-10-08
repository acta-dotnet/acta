using Acta.Testing;
using Xunit;

namespace Acta.Tests.Testing;

/// <summary>
/// <see cref="ActaTestHost"/> maps the runtime's tick outcome to the public
/// <see cref="ActaRunStatus"/> with a bare numeric cast, which is only sound while the two enums
/// agree member for member. Nothing else holds them together: the public enum is a frozen surface
/// and the internal one is free to move, so this pin is what turns a silent remap into a red test.
/// </summary>
public sealed class ActaRunStatusParityTests
{
    [Fact]
    public void Public_run_outcome_members_mirror_the_internal_tick_outcome()
    {
        foreach (var name in Enum.GetNames<ActaRunStatus>())
        {
            Assert.True(Enum.TryParse<RunOnceStatus>(name, out var internalValue), $"RunOnceStatus lacks member '{name}'.");
            Assert.Equal((byte)Enum.Parse<ActaRunStatus>(name), (byte)internalValue);
        }
    }
}
