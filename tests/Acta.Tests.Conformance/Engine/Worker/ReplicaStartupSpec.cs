using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// Replicas of one build come up together against a namespace none of them has deployed yet, as a first
/// development start with several pods does. Each is its own process here: its own container, its own worker
/// row, the same manifest. Each deploys its catalog unrecorded and then starts, so the deploys race on the same
/// new namespace, definition, and recurring slot rows, and every replica must still come up.
/// </summary>
[ConformanceSpec(
    "worker.replica-startup",
    "Replicas deploying a new namespace at the same moment all start",
    Area = "Workers",
    Contract = "Replicas that deploy the same new namespace, definitions, and recurring slots at once all start and agree on one catalog."
)]
[Deploys("add-numbers", "purge-now")]
public sealed class ReplicaStartupSpec : ActaTestBase<ConformanceFixture>
{
    private const int Replicas = 4;

    [Fact(DisplayName = "Four replicas deploying a new namespace and starting at once all start and agree on its catalog")]
    public async Task Replicas_deploying_a_new_namespace_at_once_all_start()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Unique("replicas");
        var providers = Enumerable.Range(0, Replicas).Select(i => Replica(ns, i)).ToList();
        try
        {
            var runtimes = providers.Select(p => p.GetServices<WorkerRuntime>().Single()).ToList();

            await Task.WhenAll(
                providers.Zip(runtimes).Select(r => Task.Run(() => CatalogTestOps.DeployAndStartAsync(r.First, r.Second, ct), ct))
            );

            var namespaceIds = runtimes.Select(r => r.RegisteredNamespaceIds[ns]).Distinct().ToList();
            Assert.Single(namespaceIds);
            foreach (var jobName in new[] { "add-numbers", "purge-now" })
            {
                var ids = runtimes
                    .Select(r =>
                    {
                        Assert.True(r.TryGetDefinitionId(jobName, out var id), $"{jobName} unregistered");
                        return id;
                    })
                    .Distinct();
                Assert.Single(ids);
            }
        }
        finally
        {
            foreach (var provider in providers)
            {
                await provider.DisposeAsync();
            }
        }
    }

    private ServiceProvider Replica(string ns, int index)
    {
        var services = new ServiceCollection();
        services.UseActa(j =>
        {
            Fixture.ApplyProvider(j, Schema.SchemaName);
            j.Run<JobsOf<ReplicaStartupSpec>>(ns, ownerTeam: "test", description: $"replica-{index}");
        });
        services.Configure<JobsOptions>(o => o.RunSystemJobs = true);
        return services.BuildServiceProvider(validateScopes: true);
    }
}
