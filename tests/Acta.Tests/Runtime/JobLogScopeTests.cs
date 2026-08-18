using Acta.Runtime.Modules.Execution;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The per-attempt logging-scope field contract: every log line emitted while a job runs carries the
/// job identity (id, name, namespace, execution number, worker id) and the correlation id when set.
/// These field names are the operator-facing log property names; renaming one breaks log queries.
/// </summary>
public sealed class JobLogScopeTests
{
    [Fact]
    public void Scope_carries_job_identity_fields()
    {
        var fields = Fields(JobLogScope.For(jobId: 42, jobName: "send-receipt", jobNamespace: "billing", executionNumber: 3, workerId: 7));

        Assert.Equal(42L, fields["JobId"]);
        Assert.Equal("send-receipt", fields["JobName"]);
        Assert.Equal("billing", fields["Namespace"]);
        Assert.Equal(3, fields["ExecutionNumber"]);
        Assert.Equal(7, fields["WorkerId"]);
    }

    [Fact]
    public void Scope_omits_correlation_key_when_absent()
    {
        var fields = Fields(JobLogScope.For(1, "j", "ns", 1, 1, correlationKey: null));

        Assert.DoesNotContain("CorrelationKey", fields.Keys);
    }

    [Fact]
    public void Scope_includes_correlation_key_when_present()
    {
        var fields = Fields(JobLogScope.For(1, "j", "ns", 1, 1, correlationKey: "order-abc-123"));

        Assert.Equal("order-abc-123", fields["CorrelationKey"]);
    }

    private static IReadOnlyDictionary<string, object> Fields(IReadOnlyList<KeyValuePair<string, object>> scope) =>
        scope.ToDictionary(kv => kv.Key, kv => kv.Value);
}
