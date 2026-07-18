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
        var lookup = JobLookup.ByRef(jobRef);

        Assert.Equal(JobLookupKind.JobRef, lookup.Kind);
        Assert.Equal(jobRef, lookup.JobRef);
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
        JobLookup lookup = jobRef;

        Assert.Equal(JobLookupKind.JobRef, lookup.Kind);
        Assert.Equal(jobRef, lookup.JobRef);
    }

    [Fact]
    public void ById_carries_kind_and_value()
    {
        var lookup = JobLookup.ById(42);

        Assert.Equal(JobLookupKind.JobId, lookup.Kind);
        Assert.Equal(42, lookup.JobId);
    }

    [Fact]
    public void ByDeduplicationKey_carries_kind_and_values()
    {
        var lookup = JobLookup.ByDeduplicationKey("billing", "order:7");

        Assert.Equal(JobLookupKind.DeduplicationKey, lookup.Kind);
        Assert.Equal("billing", lookup.JobNamespace);
        Assert.Equal("order:7", lookup.DeduplicationKey);
    }

    [Fact]
    public void ByDeduplicationKey_allows_stored_system_keys()
    {
        var lookup = JobLookup.ByDeduplicationKey("system", " SYS.Retention ");

        Assert.Equal("sys.retention", lookup.DeduplicationKey);
    }

    [Fact]
    public void Default_lookup_is_none()
    {
        Assert.Equal(JobLookupKind.None, default(JobLookup).Kind);
    }
}
