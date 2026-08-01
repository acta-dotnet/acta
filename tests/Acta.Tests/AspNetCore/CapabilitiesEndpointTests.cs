using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>The capabilities read exposes controlsEnabled, provider, schema, version, and the confirmation header name.</summary>
public sealed class CapabilitiesEndpointTests
{
    [Fact]
    public async Task Capabilities_reports_shape()
    {
        var (app, client) = await TestDashboardHost.StartAsync(o =>
        {
            o.EnableControls = true;
            // Non-default header name proves the value is wired from the options, not hardcoded.
            o.ControlConfirmationHeaderName = "X-Test-Confirm";
        });
        await using var _ = app;
        var res = await client.GetAsync("/acta/api/capabilities", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<CapabilitiesDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.True(body!.ControlsEnabled);
        Assert.Equal("sqlite", body.Provider);
        // No provider options registered on the fake host, so the schema falls back to the option default.
        Assert.Equal("acta", body.Schema);
        Assert.Equal("X-Test-Confirm", body.ConfirmationHeader);
        Assert.False(string.IsNullOrWhiteSpace(body.Version));
    }

    [Fact]
    public async Task Capabilities_reports_the_configured_schema()
    {
        var (app, client) = await TestDashboardHost.StartAsync(configureBuilder: b =>
            b.Services.AddSingleton<SqlProviderOptions>(new FixedSchemaOptions("ops"))
        );
        await using var _ = app;
        var res = await client.GetAsync("/acta/api/capabilities", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<CapabilitiesDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal("ops", body!.Schema);
    }

    private sealed class FixedSchemaOptions : SqlProviderOptions
    {
        public FixedSchemaOptions(string schema) => Schema = schema;
    }

    /// <summary>The read stays mapped with controls off - controlsEnabled:false IS the dashboard's signal.</summary>
    [Fact]
    public async Task Capabilities_responds_with_controls_disabled()
    {
        var (app, client) = await TestDashboardHost.StartAsync(o => o.EnableControls = false);
        await using var _ = app;
        var res = await client.GetAsync("/acta/api/capabilities", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<CapabilitiesDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.False(body!.ControlsEnabled);
        Assert.Equal("sqlite", body.Provider);
        Assert.Equal("acta", body.Schema);
        Assert.Equal("X-Acta-Control", body.ConfirmationHeader);
        Assert.False(string.IsNullOrWhiteSpace(body.Version));
    }

    private sealed record CapabilitiesDto(bool ControlsEnabled, string Version, string Provider, string Schema, string ConfirmationHeader);
}
