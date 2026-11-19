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
    public void A_success_with_a_result_answers_it_from_every_member()
    {
        var outcome = JobOutcome<string>.Landed(7, Ref, "printed");

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.HasValue);
        Assert.Equal("printed", outcome.Value);
        Assert.True(outcome.TryGetValue(out var value));
        Assert.Equal("printed", value);
        Assert.Equal("printed", outcome.ValueOrThrow());
    }

    [Fact]
    public void A_success_with_a_default_value_type_result_answers_it()
    {
        // A stored zero is a value: the outcome tells it from a success that stored none.
        var outcome = JobOutcome<int>.Landed(7, Ref, 0);

        Assert.True(outcome.HasValue);
        Assert.True(outcome.TryGetValue(out var value));
        Assert.Equal(0, value);
        Assert.Equal(0, outcome.ValueOrThrow());
    }

    [Fact]
    public void A_success_that_stored_no_result_answers_no_value_and_ValueOrThrow_names_the_job()
    {
        var label = JobOutcome<string>.Empty(7, Ref, "print-label");
        var count = JobOutcome<int>.Empty(7, Ref, "count-items");

        Assert.True(label.Succeeded);
        Assert.False(label.HasValue);
        Assert.Null(label.Value);
        Assert.False(label.TryGetValue(out _));
        Assert.True(count.Succeeded);
        Assert.False(count.HasValue);
        Assert.Equal(0, count.Value);
        Assert.False(count.TryGetValue(out _));

        var missing = Assert.Throws<JobResultMissingException>(label.ValueOrThrow);
        Assert.Equal((7L, Ref), (missing.JobId, missing.JobRef));
        Assert.Equal(
            $"Job {Ref} ('print-label') succeeded and stored no result. Read an optional result with TryGetValue.",
            missing.Message
        );
        Assert.Equal(Ref, Assert.Throws<JobResultMissingException>(() => count.ValueOrThrow()).JobRef);
    }

    [Fact]
    public void A_failed_or_timed_out_typed_outcome_has_no_value_and_ValueOrThrow_says_how_it_ended()
    {
        var failed = JobOutcome<int>.Landed(7, Ref, JobStatusCode.Failed, "card declined");
        var timedOut = JobOutcome<int>.Expired(7, Ref, JobStatusCode.Executing);

        Assert.False(failed.Succeeded);
        Assert.False(failed.HasValue);
        Assert.False(failed.TryGetValue(out _));
        Assert.Equal(0, failed.Value);
        Assert.Contains("card declined", Assert.Throws<JobFailedException>(() => failed.ValueOrThrow()).Message, StringComparison.Ordinal);
        Assert.False(timedOut.Succeeded);
        Assert.False(timedOut.HasValue);
        Assert.False(timedOut.TryGetValue(out _));
        Assert.Throws<JobWaitTimeoutException>(() => timedOut.ValueOrThrow());
    }

    [Fact]
    public void The_public_constructor_names_the_job_by_its_ref()
    {
        // A consumer builds one in a test; it names the job by its ref alone.
        var missing = new JobResultMissingException(7, Ref);

        Assert.Equal((7L, Ref), (missing.JobId, missing.JobRef));
        Assert.Equal($"Job {Ref} succeeded and stored no result. Read an optional result with TryGetValue.", missing.Message);
    }

    [Fact]
    public void A_missing_result_is_never_retried()
    {
        Assert.True(NonRetryableExceptions.IsBuiltIn(new JobResultMissingException(7, Ref)));
    }
}
