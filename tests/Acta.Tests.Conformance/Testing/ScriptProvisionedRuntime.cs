using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Runs work on a schema the published provisioning script built, through a host that leaves
/// <c>ApplyMigrationsOnStartup</c> false: the production shape, where the application principal has
/// no DDL and the startup preflight is the only schema code that runs. Shared by the three
/// provision-script specs.
/// </summary>
public static class ScriptProvisionedRuntime
{
    private const string Namespace = "provision-script";

    /// <summary>Enqueues one add-numbers job, drives it to its result, and returns its reference.</summary>
    public static async Task<JobRef> RunJobAsync(
        Action<IActaBuilder, string> applyProvider,
        string schema,
        int left,
        int right,
        CancellationToken ct
    )
    {
        await using var host = await StartAsync(applyProvider, schema, ct);
        var session = await Scenario.For<AddNumbers, AddNumbersResult>(host).EnqueueAsync(new AddNumbers(left, right), ct: ct);
        await session.RunUntilDoneAsync(ct: ct);
        Assert.Equal(new AddNumbersResult(left + right), await session.ResultAsync(ct));
        return session.JobRef;
    }

    /// <summary>Asserts that a job run earlier still reads back as succeeded with its result.</summary>
    public static async Task AssertSucceededAsync(
        Action<IActaBuilder, string> applyProvider,
        string schema,
        JobRef job,
        int sum,
        CancellationToken ct
    )
    {
        await using var host = await StartAsync(applyProvider, schema, ct);
        Assert.Equal(JobStatusCode.Succeeded, await host.Jobs.GetStatusAsync(JobLookup.ByRef(job), ct));
        Assert.Equal(new AddNumbersResult(sum), await host.Jobs.GetResultAsync<AddNumbersResult>(JobLookup.ByRef(job), ct));
    }

    private static Task<IActaTestHost> StartAsync(Action<IActaBuilder, string> applyProvider, string schema, CancellationToken ct) =>
        ActaTestHost.StartAsync(
            (j, s) =>
            {
                applyProvider(j, s);
                j.Run<TestJobsManifest>(Namespace, ownerTeam: "test", description: nameof(ScriptProvisionedRuntime));
            },
            new ActaTestHostOptions
            {
                Schema = schema,
                ConfigureServices = services => services.Configure<JobsOptions>(o => o.RegisterSystemJobs = false),
            },
            ct
        );
}
