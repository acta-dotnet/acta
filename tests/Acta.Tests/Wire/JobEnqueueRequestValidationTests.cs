using Xunit;

namespace Acta.Tests.Wire;

public sealed class JobEnqueueRequestValidationTests
{
    private const string Ns = "billing";
    private const string Name = "send-invoice";

    [Fact]
    public void NormalizeAndValidate_folds_names_and_keeps_keys_and_display_values_exact()
    {
        var request = new EnqueueJobRequest(
            "Billing",
            "Send-Invoice",
            DeduplicationKey: " Order-1 ",
            CorrelationKey: "Trace-A",
            ConcurrencyKey: "Mutex-A",
            Tags: [new TagInput("Env.Prod", "EU-West")],
            TenantKey: "Tenant-A"
        )
        {
            Lane = "ORDER-1",
        };

        var normalized = JobEnqueueRequestValidation.NormalizeAndValidate(request, nameof(request));

        Assert.Equal("billing", normalized.JobNamespace);
        Assert.Equal("send-invoice", normalized.JobName);
        Assert.Equal("Order-1", normalized.DeduplicationKey);
        Assert.Equal("Trace-A", normalized.CorrelationKey);
        Assert.Equal("Mutex-A", normalized.ConcurrencyKey);
        Assert.Equal("tenant-a", normalized.TenantKey);
        Assert.Equal("order-1", normalized.Lane);
        var tag = Assert.Single(normalized.Tags!);
        Assert.Equal("env.prod", tag.Name);
        Assert.Equal("EU-West", tag.Value);
    }

    [Fact]
    public void Only_the_deduplication_key_reserves_the_sys_prefix()
    {
        var request = new EnqueueJobRequest(Ns, Name, ConcurrencyKey: "sys.mutex", CorrelationKey: "sys.trace");

        var normalized = JobEnqueueRequestValidation.NormalizeAndValidate(request, nameof(request));

        Assert.Equal("sys.mutex", normalized.ConcurrencyKey);
        Assert.Equal("sys.trace", normalized.CorrelationKey);
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void NormalizeAndValidate_rejects_invalid_public_edge_inputs(EnqueueJobRequest request)
    {
        Assert.ThrowsAny<ArgumentException>(() => JobEnqueueRequestValidation.NormalizeAndValidate(request, nameof(request)));
    }

    public static TheoryData<EnqueueJobRequest> InvalidRequests() =>
        [
            new EnqueueJobRequest("bad namespace", Name),
            new EnqueueJobRequest("SYS", Name),
            new EnqueueJobRequest(Ns, "Sys.Send-Invoice"),
            new EnqueueJobRequest(Ns, new string('a', NameSyntax.ExtendedMaxLength + 1)),
            new EnqueueJobRequest(Ns, Name, DeduplicationKey: "sys.reserved"),
            new EnqueueJobRequest(Ns, Name, DeduplicationKey: "SYS.Reserved"),
            new EnqueueJobRequest(Ns, Name, DeduplicationKey: "has space"),
            new EnqueueJobRequest(Ns, Name, CorrelationKey: "caf\u00e9"),
            new EnqueueJobRequest(Ns, Name, TenantKey: "tenant_a"),
            new EnqueueJobRequest(Ns, Name) { Lane = "order:42" },
            new EnqueueJobRequest(Ns, Name, ConcurrencyKey: new string('x', NameSyntax.ExtendedMaxLength + 1)),
            new EnqueueJobRequest(Ns, Name, TenantKey: " "),
            new EnqueueJobRequest(Ns, Name, CorrelationKey: new string('c', NameSyntax.DefaultMaxLength + 1)),
            new EnqueueJobRequest(Ns, Name, Tags: [new TagInput("bad tag", null)]),
            new EnqueueJobRequest(Ns, Name, Tags: [new TagInput("Sys.Prod", "blue")]),
            new EnqueueJobRequest(Ns, Name, Tags: [new TagInput("ok", new string('v', NameSyntax.ExtendedMaxLength + 1))]),
            new EnqueueJobRequest(Ns, Name, Tags: [null!]),
            new EnqueueJobRequest(Ns, Name, NextRunAtUtc: DateTime.UtcNow, DelaySeconds: 1),
            new EnqueueJobRequest(Ns, Name, DelaySeconds: -1),
            new EnqueueJobRequest(Ns, Name, ParentJobId: 0),
            new EnqueueJobRequest(Ns, Name, Priority: (JobPriorityCode)255),
        ];
}
