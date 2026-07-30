using Acta.Configuration;
using Acta.Modules.Alerting;
using Acta.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Alerts;

/// <summary>
/// Conformance for startup alert channel validation: alerting definitions validate against configured
/// worker startup channels, with Off/Warn/Fail policy preserved. Disabled channels count as configured,
/// but delivery later suppresses alerts to them.
/// </summary>
[ConformanceSpec(
    "alert-channel-validation.configured",
    "Alert channel validation uses startup configuration",
    Area = "Alerts",
    Contract = "Definition AlertChannelName validates against worker startup configuration while Off, Warn, and Fail modes keep their documented behavior.",
    Arrange = "A manifest containing policy-probe routes its alerts to an ops channel that worker startup may leave missing, configure, or configure disabled.",
    Act = "Worker startup is attempted under Off, Warn, and Fail validation modes.",
    Assert = "Fail mode rejects the missing ops channel while Warn and Off allow it, and a disabled channel still counts as configured."
)]
public abstract class AlertChannelValidationSpec<TFixture> : ActaTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "Fail mode rejects a missing configured channel")]
    public async Task Fail_mode_rejects_missing_configured_channel()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InitializeWorkerAsync("acv-fail-" + TestId, AlertChannelValidationMode.Fail, configureOpsChannel: null)
        );

        Assert.Contains("does not configure that channel", ex.Message);
        Assert.Contains("Add w.AddAlertChannel(\"ops\", ...).", ex.Message);
    }

    [Fact(DisplayName = "Warn mode allows a missing configured channel")]
    public async Task Warn_mode_allows_missing_configured_channel()
    {
        await InitializeWorkerAsync("acv-warn-" + TestId, AlertChannelValidationMode.Warn, configureOpsChannel: null);
    }

    [Fact(DisplayName = "Off mode skips missing-channel validation")]
    public async Task Off_mode_skips_missing_channel_validation()
    {
        await InitializeWorkerAsync("acv-off-" + TestId, AlertChannelValidationMode.Off, configureOpsChannel: null);
    }

    [Fact(DisplayName = "Disabled channel counts as configured for validation")]
    public async Task Disabled_channel_counts_as_configured_for_validation()
    {
        await InitializeWorkerAsync(
            "acv-disabled-" + TestId,
            AlertChannelValidationMode.Fail,
            configureOpsChannel: w => w.AddAlertChannel("ops", "log", "ops", o => o.Status = AlertChannelStatusCode.Disabled)
        );
    }

    private async Task InitializeWorkerAsync(
        string namespaceName,
        AlertChannelValidationMode mode,
        Action<IWorkerBuilder>? configureOpsChannel
    )
    {
        var services = new ServiceCollection();
        services.UseActa(j =>
        {
            Fixture.ApplyProvider(j, Schema.SchemaName);
            j.Run(
                namespaceName,
                w =>
                {
                    w.AddManifest<TestJobs.TestJobsManifest>();
                    configureOpsChannel?.Invoke(w);
                }
            );
        });
        services.Configure<JobsOptions>(o =>
        {
            o.RegisterFrameworkJobs = false;
            o.AlertChannelValidationMode = mode;
        });

        using var provider = services.BuildServiceProvider(validateScopes: true);
        var runtime = provider.GetServices<WorkerRuntime>().Single();
        await runtime.InitializeAsync(TestContext.Current.CancellationToken);
    }
}
