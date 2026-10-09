using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Telemetry;

/// <summary>
/// The engine records an execution on the <c>Acta</c> meter once its completion has landed in the database, so an
/// operator's execution count follows the ledger on every provider.
/// </summary>
[ConformanceSpec(
    "telemetry.execution-count",
    "A completed execution is counted once on the Acta meter",
    Area = "Telemetry",
    Contract = "A job that runs to success adds one acta.executions measurement tagged with its namespace, its job name, and outcome succeeded."
)]
public sealed class ExecutionMetricsSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobsManifest>
{
    [Fact(DisplayName = "A succeeded run adds one acta.executions count tagged namespace, job_name, and outcome succeeded")]
    public async Task Succeeded_run_is_counted_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var meter = Services.GetRequiredService<JobMetrics>().Meter;
        var executions = new ConcurrentQueue<(long Value, Dictionary<string, object?> Tags)>();

        // Scoped to this host's meter instance: parallel hosts publish same-named instruments on meters of the same name.
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == "acta.executions")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (_, value, tags, _) => executions.Enqueue((value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)))
        );
        listener.Start();

        await EnqueueAndRunAsync("add-numbers", new AddNumbers(2, 3), ct);

        var (value, tags) = Assert.Single(executions);
        Assert.Equal(1, value);
        Assert.Equal(TestNamespace, tags["namespace"]);
        Assert.Equal("add-numbers", tags["job_name"]);
        Assert.Equal("succeeded", tags["outcome"]);
    }
}
