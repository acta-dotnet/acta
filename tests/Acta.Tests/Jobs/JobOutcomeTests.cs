using Xunit;

namespace Acta.Tests.Jobs;

public class JobOutcomeTests
{
    private static readonly JobRef Ref = JobRef.New();

    [Fact]
    public void A_failed_outcome_throws_naming_the_job_by_its_ref()
    {
        var failed = Assert.Throws<JobFailedException>(JobOutcome.Landed(7, Ref, JobStatusCode.Failed).ThrowIfFailed);
        Assert.Equal((Ref, JobStatusCode.Failed), (failed.JobRef, failed.TerminalStatus));
        Assert.Contains(Ref.ToString(), failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_timed_out_outcome_throws_naming_the_job_by_its_ref()
    {
        var timedOut = Assert.Throws<JobWaitTimeoutException>(JobOutcome.Expired(7, Ref, JobStatusCode.Ready).ThrowIfFailed);
        Assert.Equal((7L, Ref), (timedOut.JobId, timedOut.JobRef));
        Assert.Contains(Ref.ToString(), timedOut.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Succeeded_tells_the_compiler_the_value_is_set()
    {
        var outcome = JobOutcome<string>.Landed(7, Ref, "printed");

        // No '!' on Value: this line compiles only because Succeeded says the value is not null.
        Assert.Equal(7, outcome.Succeeded ? outcome.Value.Length : 0);
    }
}
