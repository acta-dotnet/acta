using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The per-attempt logging-scope field contract: every log line emitted while a job runs carries the
/// job identity (id, name, namespace, execution number, worker id) and the correlation key when set.
/// These field names are the operator-facing log property names; renaming one breaks log queries.
/// </summary>
public sealed class JobLogScopeTests
{
    [Fact]
    public void Scope_carries_job_identity_fields()
    {
        var fields = Fields(ActaLog.JobScopeState(42, "send-receipt", "billing", 3, 7, actaCorrelationKey: null));

        Assert.Equal(42L, fields["ActaJobId"]);
        Assert.Equal("send-receipt", fields["ActaJobName"]);
        Assert.Equal("billing", fields["ActaNamespace"]);
        Assert.Equal(3L, fields["ActaExecutionNumber"]);
        Assert.Equal(7L, fields["ActaWorkerId"]);
    }

    [Fact]
    public void Scope_omits_correlation_key_when_absent()
    {
        var fields = Fields(ActaLog.JobScopeState(1, "j", "ns", 1, 1, actaCorrelationKey: null));

        Assert.DoesNotContain("ActaCorrelationKey", fields.Keys);
    }

    [Fact]
    public void Scope_includes_correlation_key_when_present()
    {
        var fields = Fields(ActaLog.JobScopeState(1, "j", "ns", 1, 1, actaCorrelationKey: "order-abc-123"));

        Assert.Equal("order-abc-123", fields["ActaCorrelationKey"]);
    }

    private static IReadOnlyDictionary<string, object> Fields(IReadOnlyList<KeyValuePair<string, object>> scope) =>
        scope.ToDictionary(kv => kv.Key, kv => kv.Value);
}
