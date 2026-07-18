using Xunit;

namespace Acta.Tests.Attributes;

/// <summary>
/// The runtime fallback default for a <c>[JobSchedule]</c> with no explicit misfire is Skip
/// (forward-only, drop missed occurrences), matching the generator default and the production
/// scheduler. FireOnceCatchUp is opt-in.
/// </summary>
public sealed class JobScheduleAttributeTests
{
    [Fact]
    public void Misfire_defaults_to_skip_when_unset()
    {
        var attr = new JobScheduleAttribute("nightly", "PT5M");
        Assert.Equal(MisfireStrategyCode.Skip, attr.Misfire);
    }

    [Fact]
    public void Time_zone_defaults_to_UTC_when_unset()
    {
        var attr = new JobScheduleAttribute("nightly", "0 0 * * *");
        Assert.Equal("UTC", attr.TimeZone);
    }
}
