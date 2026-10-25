using Xunit;

namespace Acta.Tests.Runtime;

public sealed class JobContractTests
{
    private sealed record Echo(string Text);

    [Fact]
    public void Carries_manifest_type_and_job_name()
    {
        var c = new JobContract<Echo>(typeof(JobContractTests), "echo");
        Assert.Equal(typeof(JobContractTests), c.ManifestType);
        Assert.Equal("echo", c.JobName);
    }

    [Fact]
    public void ToString_is_simple_name_slash_job()
    {
        var c = new JobContract<Echo>(typeof(JobContractTests), "echo");
        Assert.Equal("JobContractTests/echo", c.ToString());
    }
}
