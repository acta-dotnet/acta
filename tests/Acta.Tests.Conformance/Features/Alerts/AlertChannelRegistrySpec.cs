using Acta.Configuration;
using Acta.Features.Alerts;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Alerts;

/// <summary>
/// Conformance for the in-memory alert channel registry built from <c>w.AddAlertChannel</c> declarations:
/// every worker namespace gets an implicit <c>"default"</c> log channel, declarations override it in
/// memory, duplicate names are last-write-wins, and namespaces stay isolated.
/// </summary>
[ConformanceSpec(
    "alert-channel-registry.configured",
    "Alert channel registry is built from worker startup configuration",
    Area = "Alerts",
    Contract = "The registry provides a default channel per namespace, applies builder declarations as last-write-wins, and isolates namespaces.",
    Arrange = "Two worker namespaces are configured, one with a default channel override and duplicate ops-oncall declarations and one with no declarations.",
    Act = "The in-memory registry is read for both namespaces without touching SQL transport configuration.",
    Assert = "Each namespace resolves a default channel, duplicate declarations are last-write-wins, and channel names stay isolated per namespace."
)]
public abstract class AlertChannelRegistrySpec<TFixture> : ActaTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private string PrimaryNamespace => "acr-" + TestId;

    private string OtherNamespace => "acr-other-" + TestId;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        services.UseActa(j =>
        {
            Fixture.ApplyProvider(j, Schema.SchemaName);
            j.Run(
                PrimaryNamespace,
                w =>
                    w.AddManifest<TestJobs.TestJobsManifest>()
                        .AddAlertChannel(
                            "default",
                            "slack-webhook",
                            "https://hooks.example/default",
                            o => o.MinSeverity = AlertSeverityCode.Warning
                        )
                        .AddAlertChannel("ops-oncall", "log", "ops-log")
                        .AddAlertChannel(
                            "ops-oncall",
                            "slack-webhook",
                            "https://hooks.example/ops",
                            o => o.MinSeverity = AlertSeverityCode.Error
                        )
            );
            j.Run(OtherNamespace, w => w.AddManifest<TestJobs.TestJobsManifest>());
        });
        services.Configure<JobsOptions>(o => o.RegisterFrameworkJobs = false);
    }

    [Fact(DisplayName = "Default exists per namespace and declarations override in memory")]
    public void Default_exists_per_namespace_and_declarations_override()
    {
        var registry = Services.GetRequiredService<IAlertChannelRegistry>();

        var overriddenDefault = registry.Resolve(PrimaryNamespace, "default");
        Assert.NotNull(overriddenDefault);
        Assert.Equal("slack-webhook", overriddenDefault!.TransportKind);
        Assert.Equal("https://hooks.example/default", overriddenDefault.Endpoint);
        Assert.Equal(AlertSeverityCode.Warning, overriddenDefault.MinSeverity);

        var implicitOtherDefault = registry.Resolve(OtherNamespace, "default");
        Assert.NotNull(implicitOtherDefault);
        Assert.Equal("log", implicitOtherDefault!.TransportKind);
        Assert.Equal("default", implicitOtherDefault.Endpoint);
        Assert.Equal(AlertSeverityCode.Info, implicitOtherDefault.MinSeverity);
    }

    [Fact(DisplayName = "Duplicate channel declarations are last-write-wins and namespace-isolated")]
    public void Duplicate_declarations_are_last_write_wins_and_namespace_isolated()
    {
        var registry = Services.GetRequiredService<IAlertChannelRegistry>();

        var ops = registry.Resolve(PrimaryNamespace, "ops-oncall");
        Assert.NotNull(ops);
        Assert.Equal("slack-webhook", ops!.TransportKind);
        Assert.Equal("https://hooks.example/ops", ops.Endpoint);
        Assert.Equal(AlertSeverityCode.Error, ops.MinSeverity);

        Assert.Null(registry.Resolve(OtherNamespace, "ops-oncall"));
        Assert.Equal(["default", "ops-oncall"], registry.NamesForNamespace(PrimaryNamespace));
        Assert.Equal(["default"], registry.NamesForNamespace(OtherNamespace));
    }
}
