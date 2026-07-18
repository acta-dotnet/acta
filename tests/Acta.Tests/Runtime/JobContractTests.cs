using Xunit;

namespace Acta.Tests.Runtime;

public sealed class JobContractTests
{
    private sealed record Echo(string Text);

    private sealed record EchoResult(string Text);

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

    [Fact]
    public void Result_contract_converts_implicitly_to_input_only()
    {
        var withResult = new JobContract<Echo, EchoResult>(typeof(JobContractTests), "echo");
        JobContract<Echo> inputOnly = withResult;
        Assert.Equal(withResult.ManifestType, inputOnly.ManifestType);
        Assert.Equal(withResult.JobName, inputOnly.JobName);
    }
}
