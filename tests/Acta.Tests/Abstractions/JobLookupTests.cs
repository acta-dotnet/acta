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
}
