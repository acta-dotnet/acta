using Acta.Runtime;
using Xunit;

namespace Acta.Tests.FrameworkJobs;

public class FrameworkJobPrioritySpec
{
    [Fact]
    public void Framework_jobs_register_with_system_prefix()
    {
        var names = RuntimeJobs.Descriptors.Descriptors.Select(d => d.JobName).OrderBy(static n => n, StringComparer.Ordinal).ToArray();

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

    [Theory]
    [InlineData("sys.recovery")]
    [InlineData("sys.retention")]
    public void Framework_maintenance_jobs_claim_at_critical_priority(string jobName)
    {
        var descriptor = Assert.Single(RuntimeJobs.Descriptors.Descriptors, d => d.JobName == jobName);
        Assert.Equal(JobPriorityCode.Critical, descriptor.Priority);
    }
}
