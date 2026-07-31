using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for cross-namespace children: a handler starts a child in another namespace and the
/// child's terminal landing releases the waiting parent across the namespace boundary.
/// </summary>
[ConformanceSpec(
    "child-jobs.cross-namespace",
    "A child started in another namespace releases its waiting parent",
    Area = "ChildJobs",
    Contract = "StartChildAsync targets any namespace and the child's terminal landing releases the waiting parent across the namespace boundary.",
    Arrange = "Two worker runtimes serve sibling namespaces from one process.",
    Act = "A parent in one namespace starts and waits on a child routed to the second namespace, and the child completes.",
    Assert = "The child's terminal landing releases the waiting parent across the namespace boundary."
)]
public abstract class ChildJobCrossNamespaceSpec<TFixture> : ActaTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private WorkerRuntime _parentRuntime = null!;
    private WorkerRuntime _childRuntime = null!;

    private IJobs Jobs => Services.GetRequiredService<IJobs>();

    private string ChildNamespace => DeriveChildNamespace(TestNamespace);

    // The generated test namespace can already sit at the 64-char identifier cap; shorten before
    // suffixing so the sibling namespace stays valid kebab.
    private static string DeriveChildNamespace(string testNamespace) =>
        testNamespace[..Math.Min(testNamespace.Length, 60)].TrimEnd('-') + "-b";

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        services.UseActa(j =>
        {
            Fixture.ApplyProvider(j, Schema.SchemaName);
            j.Run<TestJobs.TestJobsManifest>(testNamespace, ownerTeam: "test", description: GetType().FullName);
            j.Run<TestJobs.TestJobsManifest>(DeriveChildNamespace(testNamespace), ownerTeam: "test", description: GetType().FullName);
        });
        services.Configure<JobsOptions>(o => o.RegisterFrameworkJobs = false);
    }

    protected override async ValueTask AfterInitializeAsync()
    {
        var runtimes = Services.GetServices<WorkerRuntime>().ToArray();
        foreach (var runtime in runtimes)
        {
            await runtime.InitializeAsync(TestContext.Current.CancellationToken);
        }

        _parentRuntime = runtimes.Single(r => r.RegisteredNamespaceIds.ContainsKey(TestNamespace));
        _childRuntime = runtimes.Single(r => r.RegisteredNamespaceIds.ContainsKey(ChildNamespace));
    }

    [Fact(DisplayName = "A child completing in another namespace carries the parent link and releases the waiting parent")]
    public async Task Child_in_another_namespace_releases_the_waiting_parent()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "job-parent-cross-namespace", JobPayload.Json(new CrossNamespaceStart(ChildNamespace))),
            ct
        );

        Assert.Equal(RunOnceOutcome.Rearmed, await _parentRuntime.RunOnceAsync(parent, ct));

        var child = await Db.From<Job>().Where(j => j.ParentId == parent.JobId).SingleOrDefaultAsync(ct);
        Assert.NotNull(child);
        Assert.Equal(_childRuntime.RegisteredNamespaceIds[ChildNamespace], child!.NamespaceId);
        Assert.Equal(parent.JobId, child.LineageRootId);

        Assert.Equal(RunOnceOutcome.Completed, await _childRuntime.RunOnceAsync(child.Id, ct));

        Assert.Equal(RunOnceOutcome.Completed, await _parentRuntime.RunOnceAsync(parent, ct));
        var outcome = await Jobs.GetResultAsync<ChildJobOutcome>(parent, ct);
        Assert.True(outcome!.Succeeded);
        Assert.Equal(child.Id, outcome.ChildJobId);
    }
}
