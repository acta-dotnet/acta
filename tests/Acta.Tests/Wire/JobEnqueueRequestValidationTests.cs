using Xunit;

namespace Acta.Tests.Wire;

public sealed class JobEnqueueRequestValidationTests
{
    private const string Ns = "billing";
    private const string Name = "send-invoice";

    [Fact]
    public void NormalizeAndValidate_normalizes_keys_without_touching_external_or_display_values()
    {
        var request = new JobEnqueueRequest(
            "billing",
            "send-invoice",
            DeduplicationKey: "Order-1",
            CorrelationKey: "Trace-A",
            ExclusiveKey: "Mutex-A",
            Tags: [new TagInput("env.prod", "EU-West")],
            TenantKey: "Tenant-A"
        );

        var normalized = JobEnqueueRequestValidation.NormalizeAndValidate(request, nameof(request));

        Assert.Equal("billing", normalized.JobNamespace);
        Assert.Equal("send-invoice", normalized.JobName);
        Assert.Equal("order-1", normalized.DeduplicationKey);
        Assert.Equal("Trace-A", normalized.CorrelationKey);
        Assert.Equal("mutex-a", normalized.ExclusiveKey);
        Assert.Equal("tenant-a", normalized.TenantKey);
        var tag = Assert.Single(normalized.Tags!);
        Assert.Equal("env.prod", tag.Name);
        Assert.Equal("EU-West", tag.Value);
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void NormalizeAndValidate_rejects_invalid_public_edge_inputs(JobEnqueueRequest request)
    {
        Assert.ThrowsAny<ArgumentException>(() => JobEnqueueRequestValidation.NormalizeAndValidate(request, nameof(request)));
    }

    public static TheoryData<JobEnqueueRequest> InvalidRequests() =>
        new()
        {
            new JobEnqueueRequest("bad namespace", Name),
            new JobEnqueueRequest("Billing", Name),
            new JobEnqueueRequest(Ns, "Send-Invoice"),
            new JobEnqueueRequest(Ns, new string('a', IdentifierSyntax.ExtendedMaxLength + 1)),
            new JobEnqueueRequest(Ns, Name, DeduplicationKey: "sys.reserved"),
            new JobEnqueueRequest(Ns, Name, ExclusiveKey: new string('x', IdentifierSyntax.ExtendedMaxLength + 1)),
            new JobEnqueueRequest(Ns, Name, TenantKey: " "),
            new JobEnqueueRequest(Ns, Name, CorrelationKey: new string('c', IdentifierSyntax.DefaultMaxLength + 1)),
            new JobEnqueueRequest(Ns, Name, Tags: [new TagInput("bad tag", null)]),
            new JobEnqueueRequest(Ns, Name, Tags: [new TagInput("Env.Prod", "blue")]),
            new JobEnqueueRequest(Ns, Name, Tags: [new TagInput("ok", new string('v', IdentifierSyntax.ExtendedMaxLength + 1))]),
            new JobEnqueueRequest(Ns, Name, Tags: [null!]),
            new JobEnqueueRequest(Ns, Name, NextRunAtUtc: DateTime.UtcNow, DelaySeconds: 1),
            new JobEnqueueRequest(Ns, Name, DelaySeconds: -1),
            new JobEnqueueRequest(Ns, Name, ParentId: 0),
            new JobEnqueueRequest(Ns, Name, Priority: (JobPriorityCode)255),
        };
}
