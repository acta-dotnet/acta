using Xunit;

namespace Acta.Tests.Jobs;

public class JobOutcomeTests
{
    private static readonly JobRef Ref = JobRef.New();

    [Fact]
    public void A_failed_outcome_throws_naming_the_job_by_its_ref()
    {
        var failed = Assert.Throws<JobFailedException>(JobOutcome.Failed(7, Ref).ThrowIfFailed);
        Assert.Equal((Ref, JobStatusCode.Failed), (failed.JobRef, failed.TerminalStatus));
        Assert.Contains(Ref.ToString(), failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_timed_out_outcome_throws_naming_the_job_by_its_ref()
    {
        var timedOut = Assert.Throws<TimeoutException>(JobOutcome.TimedOut(7, Ref, JobStatusCode.Ready).ThrowIfFailed);
        Assert.Contains(Ref.ToString(), timedOut.Message, StringComparison.Ordinal);
    }
}
