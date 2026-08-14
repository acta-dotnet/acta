using Xunit;

namespace Acta.Tests.Abstractions;

/// <summary>
/// Anchors the three JobLookup identities: public JobRef, user DeduplicationKey, and internal JobId.
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

    [Fact]
    public void ByRef_rejects_empty_ref()
    {
        Assert.Throws<ArgumentException>(() => JobLookup.ByRef(default));
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
    public void ByDeduplicationKey_carries_kind_and_values()
    {
        var job = JobLookup.ByDeduplicationKey("billing", "order:7");

        Assert.Equal(JobLookupKind.DeduplicationKey, job.Kind);
        Assert.Equal("billing", job.JobNamespace);
        Assert.Equal("order:7", job.DeduplicationKey);
    }

    [Fact]
    public void ByDeduplicationKey_allows_stored_system_keys()
    {
        var job = JobLookup.ByDeduplicationKey("system", " SYS.Retention ");

        Assert.Equal("sys.retention", job.DeduplicationKey);
    }

    [Fact]
    public void Default_lookup_is_none()
    {
        Assert.Equal(JobLookupKind.None, default(JobLookup).Kind);
    }
}
