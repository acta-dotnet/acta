using Acta.Tests.AspNetCore.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// A <see cref="ActaEndpointOptions.MaxReasonMessageLength"/> below 1 would reject every control
/// request the confirmation lets through, so both mapping entry points reject it
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
        builder.Services.AddSingleton<IJobs>(new FakeJobs());
        var app = builder.Build();

        Assert.Throws<ArgumentOutOfRangeException>(() => app.MapActaApi("/acta/api", o => o.MaxReasonMessageLength = -1));
    }

    [Fact]
    public async Task MapActa_accepts_the_default_MaxReasonMessageLength()
    {
        var (app, _) = await TestDashboardHost.StartAsync();
        await using var _2 = app;
    }

    /// <summary>
    /// A confirmation header a page on any origin may send without a preflight would let a cross-site form confirm a
    /// control, so mapping refuses the five CORS-safelisted request headers in any casing, and an empty name.
    /// </summary>
    [Theory]
    [InlineData("accept")]
    [InlineData("ACCEPT-LANGUAGE")]
    [InlineData("Content-language")]
    [InlineData("content-TYPE")]
    [InlineData("rAnGe")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MapActa_rejects_a_confirmation_header_any_page_may_send(string name)
    {
        var thrown = await Assert.ThrowsAsync<ArgumentException>(() =>
            TestDashboardHost.StartAsync(configureDashboard: o => o.ControlConfirmationHeaderName = name)
        );

        Assert.Contains("ControlConfirmationHeaderName", thrown.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("X-Acta-Control")]
    [InlineData("X-Ops-Confirm")]
    public async Task MapActa_accepts_a_confirmation_header_a_page_must_preflight(string name)
    {
        var (app, _) = await TestDashboardHost.StartAsync(configureDashboard: o => o.ControlConfirmationHeaderName = name);
        await using var _2 = app;
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
