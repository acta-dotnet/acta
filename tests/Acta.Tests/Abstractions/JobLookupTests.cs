using System.Globalization;
using Xunit;

namespace Acta.Tests.Abstractions;

/// <summary>
/// Anchors the three JobLookup identities: public JobRef, user IdempotencyKey, and internal JobId.
/// </summary>
public class JobLookupTests
{
    [Fact]
    public void ByRef_carries_kind_and_value()
    {
        var jobRef = JobRef.New();
        var job = JobLookup.ByRef(jobRef);

        Assert.Equal(JobLookupKind.JobRef, job.Kind);
        Assert.Equal(jobRef, job.JobRef);
    }

    /// <summary>The all-zero ref is well formed and names no job, so it builds a lookup that finds nothing rather than throwing.</summary>
    [Fact]
    public void ByRef_takes_the_all_zero_ref()
    {
        Assert.Equal(default, JobLookup.ByRef(default).JobRef);
    }

    [Fact]
    public void JobRef_converts_implicitly()
    {
        var jobRef = JobRef.New();
        JobLookup job = jobRef;

        Assert.Equal(JobLookupKind.JobRef, job.Kind);
        Assert.Equal(jobRef, job.JobRef);
    }

    /// <summary>A ref that travelled as a plain Guid addresses the same job as the ref itself.</summary>
    [Fact]
    public void Guid_converts_implicitly_to_the_lookup_of_its_ref()
    {
        var jobRef = JobRef.New();
        Guid carried = jobRef;
        JobLookup job = carried;

        Assert.Equal(jobRef.Value, carried);
        Assert.Equal(JobLookup.ByRef(jobRef), job);
    }

    [Fact]
    public void ById_carries_kind_and_value()
    {
        var job = JobLookup.ById(42);

        Assert.Equal(JobLookupKind.JobId, job.Kind);
        Assert.Equal(42, job.JobId);
    }

    [Fact]
    public void ByIdempotencyKey_carries_kind_and_values()
    {
        var job = JobLookup.ByIdempotencyKey("billing", "order:7");

        Assert.Equal(JobLookupKind.IdempotencyKey, job.Kind);
        Assert.Equal("billing", job.JobNamespace);
        Assert.Equal("order:7", job.IdempotencyKey);
    }

    [Fact]
    public void ByIdempotencyKey_allows_stored_system_keys()
    {
        var job = JobLookup.ByIdempotencyKey(" System ", " sys.retention ");

        Assert.Equal("system", job.JobNamespace);
        Assert.Equal("sys.retention", job.IdempotencyKey);
        Assert.Equal("Order-7", JobLookup.ByIdempotencyKey("billing", "Order-7").IdempotencyKey);
    }

    [Fact]
    public void Default_lookup_is_none()
    {
        Assert.Equal(JobLookupKind.None, default(JobLookup).Kind);
    }

    /// <summary>A ref Acta mints is a version 7 Guid, so a lookup by it that finds no job names when it was minted.</summary>
    [Fact]
    public void A_minted_ref_that_names_no_job_says_when_it_was_minted_and_what_may_have_happened()
    {
        var jobRef = JobRef.New();
        var hint =
            $"The ref was minted at {MintTime(jobRef)}. If a job held it, the job's retention or a purge may have removed it; "
            + "a ref minted moments ago may belong to a transaction that has not committed.";

        Assert.Equal(hint, JobLookup.ByRef(jobRef).NotFoundHint());
        Assert.Equal($"No job matches {jobRef}. {hint}", JobLookup.ByRef(jobRef).NoJobMessage());
    }

    /// <summary>A random ref, the all-zero ref, and a version 7 shape with a time no ref can have get the plain message.</summary>
    [Theory]
    [InlineData("job_3f2b8c1e9d4a4f6b8e7c5a2d1b0f9e8d")]
    [InlineData("job_00000000000000000000000000000000")]
    [InlineData("job_00000000000070008000000000000000")]
    [InlineData("job_ffffffffffff7fff8fffffffffffffff")]
    public void Any_other_ref_gets_the_plain_message(string text)
    {
        var lookup = JobLookup.ByRef(JobRef.Parse(text));

        Assert.Null(lookup.NotFoundHint());
        Assert.Equal($"No job matches {text}.", lookup.NoJobMessage());
    }

    [Fact]
    public void A_lookup_with_no_ref_names_what_it_was_given()
    {
        Assert.Null(JobLookup.ById(42).NotFoundHint());
        Assert.Equal("No job matches job id 42.", JobLookup.ById(42).NoJobMessage());
        Assert.Equal(
            "No job matches idempotency key 'order:7' in namespace 'billing'.",
            JobLookup.ByIdempotencyKey("billing", "order:7").NoJobMessage()
        );
    }

    /// <summary>An enqueue's answer, a handle included, reads by its id and still names its ref when the job is gone.</summary>
    [Fact]
    public void An_enqueue_response_reads_by_id_and_keeps_its_ref_for_the_message()
    {
        var jobRef = JobRef.New();
        JobLookup job = new EnqueueJobResponse(42, jobRef, EnqueueJobAction.Inserted);

        Assert.Equal(JobLookupKind.JobId, job.Kind);
        Assert.Equal(42, job.JobId);
        Assert.StartsWith(
            $"No job matches {jobRef}. The ref was minted at {MintTime(jobRef)}.",
            job.NoJobMessage(),
            StringComparison.Ordinal
        );
    }

    /// <summary>The first 12 hex digits of a version 7 ref are its Unix time in milliseconds.</summary>
    private static string MintTime(JobRef jobRef) =>
        DateTimeOffset
            .FromUnixTimeMilliseconds(long.Parse(jobRef.ToString()[4..16], NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            .UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
