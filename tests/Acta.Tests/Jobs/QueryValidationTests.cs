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
    public void ValidateNamespace_folds_mixed_case_rejects_malformed_and_passes_null_through()
    {
        Assert.Equal("sys", QueryValidation.ValidateNamespace("Sys", "namespace"));
        Assert.Throws<InvalidQueryException>(() => QueryValidation.ValidateNamespace("bill_ing", "namespace"));
        Assert.Null(QueryValidation.ValidateNamespace(null, "namespace"));
    }

    [Fact]
    public void ValidateJobName_accepts_sys_dot_prefixed_job_names_in_any_case()
    {
        Assert.Equal("sys.retention", QueryValidation.ValidateJobName("sys.retention", "sys", "jobName"));
        Assert.Equal("sys.retention", QueryValidation.ValidateJobName("Sys.Retention", "sys", "jobName"));
    }

    [Fact]
    public void Name_fragments_fold_and_keep_the_like_wildcards_out()
    {
        Assert.Equal("send-inv", QueryValidation.ValidateJobNameFragment("Send-Inv", "nameContains"));
        Assert.Equal("bill", QueryValidation.ValidateNamespaceFragment("BILL", "nameContains"));
        Assert.Throws<InvalidQueryException>(() => QueryValidation.ValidateJobNameFragment("send_%", "nameContains"));
    }
}
