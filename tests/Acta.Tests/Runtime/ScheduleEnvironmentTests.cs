using System.Collections.Immutable;
using Acta.Modules.Execution.Schedules;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// Environment gating for declared schedules (<see cref="ScheduleEnvironment.IsActiveIn"/>): a
/// schedule with no declared environments is a wildcard active everywhere; otherwise it registers only
/// when the worker's current environment matches one of its names, case-insensitively. Mirrors .NET
/// host-environment comparison semantics.
/// </summary>
public class ScheduleEnvironmentTests
{
    [Fact]
    public void Default_array_is_a_wildcard_active_in_every_environment()
    {
        Assert.True(ScheduleEnvironment.IsActiveIn(default, "production"));
        Assert.True(ScheduleEnvironment.IsActiveIn(default, null));
    }

    [Fact]
    public void Empty_array_is_a_wildcard_active_in_every_environment()
    {
        Assert.True(ScheduleEnvironment.IsActiveIn(ImmutableArray<string>.Empty, "staging"));
    }

    [Fact]
    public void Exact_environment_name_match_is_active()
    {
        Assert.True(ScheduleEnvironment.IsActiveIn(["production"], "production"));
    }

    [Fact]
    public void Match_is_case_insensitive()
    {
        Assert.True(ScheduleEnvironment.IsActiveIn(["Production"], "production"));
        Assert.True(ScheduleEnvironment.IsActiveIn(["production"], "PRODUCTION"));
    }

    [Fact]
    public void Non_matching_environment_is_inactive()
    {
        Assert.False(ScheduleEnvironment.IsActiveIn(["production"], "staging"));
    }

    [Fact]
    public void One_match_among_several_declared_environments_is_active()
    {
        Assert.True(ScheduleEnvironment.IsActiveIn(["staging", "production"], "production"));
        Assert.False(ScheduleEnvironment.IsActiveIn(["staging", "qa"], "production"));
    }

    [Fact]
    public void Scoped_schedule_is_inactive_when_the_current_environment_is_unknown()
    {
        Assert.False(ScheduleEnvironment.IsActiveIn(["production"], null));
        Assert.False(ScheduleEnvironment.IsActiveIn(["production"], ""));
    }
}
