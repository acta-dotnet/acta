using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// A <see cref="ActaEndpointOptions.MaxReasonMessageLength"/> below 1 would reject every control
/// request the confirmation-header guard lets through, so both mapping entry points reject it
/// eagerly (a bootstrap guard: these options are constructed with <c>new()</c>, not through the
/// DI options pipeline).
/// </summary>
public sealed class ActaEndpointOptionsValidationTests
{
    [Fact]
    public async Task MapActa_rejects_non_positive_MaxReasonMessageLength()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            TestDashboardHost.StartAsync(configureDashboard: o => o.MaxReasonMessageLength = 0)
        );
    }

    [Fact]
    public void MapActaApi_rejects_non_positive_MaxReasonMessageLength()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IJobs>(new TestDashboardHost.FakeJobs());
        var app = builder.Build();

        Assert.Throws<ArgumentOutOfRangeException>(() => app.MapActaApi("/acta/api", o => o.MaxReasonMessageLength = -1));
    }

    [Fact]
    public async Task MapActa_accepts_the_default_MaxReasonMessageLength()
    {
        var (app, _) = await TestDashboardHost.StartAsync();
        await using var _2 = app;
    }

    [Fact]
    public async Task MapActa_rejects_non_positive_MaxRequestBodyBytes()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            TestDashboardHost.StartAsync(configureDashboard: o => o.MaxRequestBodyBytes = 0)
        );
    }

    [Fact]
    public async Task LocalOnly_false_without_authorization_or_acknowledgement_throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestDashboardHost.StartAsync(configureDashboard: o => o.LocalOnly = false)
        );
    }

    [Fact]
    public async Task LocalOnly_false_with_an_endpoint_configuration_hook_maps()
    {
        var (app, _) = await TestDashboardHost.StartAsync(configureDashboard: o =>
        {
            o.LocalOnly = false;
            o.ConfigureEndpoints = group => { };
        });
        await using var _2 = app;
    }

    [Fact]
    public async Task LocalOnly_false_with_the_unsafe_acknowledgement_maps()
    {
        var (app, _) = await TestDashboardHost.StartAsync(configureDashboard: o =>
        {
            o.LocalOnly = false;
            o.UnsafeAllowAnonymousRemoteAccess = true;
        });
        await using var _2 = app;
    }
}
