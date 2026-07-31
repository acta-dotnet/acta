using Acta.Runtime.Querying;
using Xunit;

namespace Acta.Tests.Jobs;

/// <summary>
/// Unit tests for the dashboard/list-filter validators in <see cref="QueryValidation"/>: the
/// lookup-permissive canonicalization path. Unlike the registration validators, these must keep
/// accepting the bare <c>sys</c> namespace and <c>sys.*</c> job names so operators can filter to the
/// seeded system namespace's audit events.
/// </summary>
public sealed class QueryValidationTests
{
    [Fact]
    public void ValidateNamespace_accepts_the_bare_sys_namespace()
    {
        Assert.Equal("sys", QueryValidation.ValidateNamespace("sys", "namespace"));
    }

    [Fact]
    public void ValidateNamespace_still_rejects_mixed_case_and_null_passthrough()
    {
        Assert.Throws<InvalidQueryException>(() => QueryValidation.ValidateNamespace("Sys", "namespace"));
        Assert.Null(QueryValidation.ValidateNamespace(null, "namespace"));
    }

    [Fact]
    public void ValidateJobName_accepts_sys_dot_prefixed_job_names()
    {
        Assert.Equal("sys.recovery", QueryValidation.ValidateJobName("sys.recovery", "sys", "jobName"));
    }
}
