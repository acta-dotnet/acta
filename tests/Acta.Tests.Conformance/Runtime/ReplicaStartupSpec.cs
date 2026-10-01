using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// Replicas of one build start together against a namespace none of them has registered yet, as a
/// first deploy with several pods does. Each is its own process here: its own container, its own worker
/// row, the same manifest. The registrations race on the same new namespace, definition, and recurring
/// slot rows, and the one that commits second must still come up.
/// </summary>
[ConformanceSpec(
    "worker.replica-startup",
    "Replicas registering a new namespace at the same moment all start",
    Area = "Workers",
    Contract = "Replicas that register the same new namespace, definitions, and recurring slots at once all start and agree on one catalog.",
    Arrange = "Four worker containers share one manifest and one namespace that no worker has registered yet.",
    Act = "All four initialize at the same moment.",
    Assert = "Every initialization completes and all four resolve the same namespace and definition ids."
)]
public abstract class ReplicaStartupSpec<TFixture> : ActaTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private const int Replicas = 4;

    [Fact(DisplayName = "Four replicas initializing a new namespace at once all start and agree on its catalog")]
    public async Task Replicas_initializing_a_new_namespace_at_once_all_start()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = TestKey("replicas");
        var providers = Enumerable.Range(0, Replicas).Select(i => Replica(ns, i)).ToList();
        try
        {
            var runtimes = providers.Select(p => p.GetServices<WorkerRuntime>().Single()).ToList();

            await Task.WhenAll(runtimes.Select(r => Task.Run(() => r.InitializeAsync(ct), ct)));

            var namespaceIds = runtimes.Select(r => r.RegisteredNamespaceIds[ns]).Distinct().ToList();
            Assert.Single(namespaceIds);
            foreach (var jobName in new[] { "add-numbers", "purge-now" })
            {
                var ids = runtimes
                    .Select(r =>
                    {
                        Assert.True(r.TryGetDefinitionId(ns, jobName, out var id), $"{jobName} unregistered");
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
            j.Run<TestJobs.TestJobsManifest>(ns, ownerTeam: "test", description: $"replica-{index}");
        });
        services.Configure<JobsOptions>(o => o.RegisterSystemJobs = true);
        return services.BuildServiceProvider(validateScopes: true);
    }
}
