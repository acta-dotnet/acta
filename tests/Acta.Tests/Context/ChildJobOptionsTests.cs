using Xunit;

namespace Acta.Tests.Context;

public sealed class ChildJobOptionsTests
{
    private readonly record struct Probe;

    [Fact]
    public async Task Typed_StartChildAsync_preserves_configured_tenant_key()
    {
        var ctx = new RecordingJobContext();

        await ctx.StartChildAsync("send-email", new Probe(), o => o.TenantKey("Tenant-A"), TestContext.Current.CancellationToken);

        var options = Assert.Single(ctx.StartOptions);
        Assert.Equal("tenant-a", options.TenantKey);
        Assert.Equal(ctx.JobId, options.ParentJobId);
        Assert.Equal("send-email", options.DeduplicationKey);
    }

    [Fact]
    public async Task Raw_StartChildAsync_preserves_configured_tenant_key()
    {
        var ctx = new RecordingJobContext();

        await ctx.StartChildAsync(
            "send-email",
            "mail",
            "send-email",
            configure: o => o.TenantKey("Tenant-A"),
            ct: TestContext.Current.CancellationToken
        );

        var request = Assert.Single(ctx.RawStarted);
        Assert.Equal("tenant-a", request.TenantKey);
        Assert.Equal(ctx.JobId, request.ParentJobId);
        Assert.Equal("send-email", request.DeduplicationKey);
    }
}
