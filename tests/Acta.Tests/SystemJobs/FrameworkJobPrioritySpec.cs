using Xunit;

namespace Acta.Tests.SystemJobs;

public class FrameworkJobPrioritySpec
{
    [Fact]
    public void Framework_jobs_register_with_system_prefix()
    {
        var names = ActaJobs.Descriptors.Descriptors.Select(d => d.JobName).OrderBy(static n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(["sys.alerts", "sys.outbox", "sys.recovery", "sys.retention"], names);
        Assert.All(
            names,
            static name =>
            {
                Assert.StartsWith("sys.", name, StringComparison.Ordinal);
                Assert.False(name.StartsWith("acta.", StringComparison.Ordinal));
            }
        );
    }

    // Claims are in strict priority order, so a system slot below Realtime would wait out any Realtime backlog.
    [Theory]
    [InlineData("sys.alerts")]
    [InlineData("sys.outbox")]
    [InlineData("sys.recovery")]
    [InlineData("sys.retention")]
    public void Framework_jobs_claim_at_realtime_priority(string jobName)
    {
        var descriptor = Assert.Single(ActaJobs.Descriptors.Descriptors, d => d.JobName == jobName);
        Assert.Equal(JobPriorityCode.Realtime, descriptor.Priority);
    }
}
