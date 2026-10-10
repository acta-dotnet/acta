using Xunit;

namespace Acta.Tests.Context;

public sealed class ChildJobOptionsTests
{
    private readonly record struct Probe;

    [Fact]
    public async Task Typed_StartChildAsync_preserves_configured_tenant_name()
    {
        var ctx = new RecordingJobContext();

        await ctx.StartChildAsync("send-email", new Probe(), o => o.TenantName("Tenant-A"), TestContext.Current.CancellationToken);

        var options = Assert.Single(ctx.StartOptions);
        Assert.Equal("tenant-a", options.TenantName);
        Assert.Equal(ctx.JobId, options.ParentJobId);
        Assert.Equal("send-email", options.DeduplicationKey);
    }

    [Fact]
    public async Task Raw_StartChildAsync_preserves_configured_tenant_name()
    {
        var ctx = new RecordingJobContext();

        await ctx.StartChildAsync(
            "send-email",
            "mail",
            "send-email",
            configure: o => o.TenantName("Tenant-A"),
            ct: TestContext.Current.CancellationToken
        );

        var request = Assert.Single(ctx.RawStarted);
        Assert.Equal("tenant-a", request.TenantName);
        Assert.Equal(ctx.JobId, request.ParentJobId);
        Assert.Equal("send-email", request.DeduplicationKey);
    }
}
