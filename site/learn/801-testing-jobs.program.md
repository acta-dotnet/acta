# The whole program: test a handler with the test host

The how-to is [/learn/801-testing-jobs.md](/learn/801-testing-jobs.md). This file holds every source file of `concepts/800-testing/801-testing-jobs` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### EchoJobTests.cs

```csharp
// How to test a handler with the test host
// `Acta.Testing` runs the real runtime on a real database with no background loop: `RunOnceAsync` decides when a job
// executes, so a test drives one attempt at a time. Run it with `dotnet test concepts/800-testing/801-testing-jobs`.
using Acta.Testing;
using Xunit;

namespace Acta.Concepts.Testing;

public sealed class EchoJobTests(ActaHostFixture acta) : ActaTestBase(acta)
{
    [Fact]
    public async Task Echo_runs_when_driven_and_returns_the_message()
    {
        var enqueued = await Jobs.EnqueueAsync(new Echo("hello"), ct: Ct);

        // Nothing executes until the test drives a tick.
        Assert.Equal(JobStatusCode.Ready, (await enqueued.GetStatusAsync(Ct))?.Status);

        // learn: 2. Drive one attempt and assert what became of it
        // `RunOnceAsync` runs one attempt of that job and answers its outcome.
        var outcome = await Host.RunOnceAsync(enqueued, Ct);

        Assert.Equal(ActaRunStatus.Completed, outcome);
        // learn: end
        Assert.Equal(JobStatusCode.Succeeded, (await enqueued.GetStatusAsync(Ct))?.Status);
        var result = await enqueued.GetResultAsync<EchoResult>(Ct);
        Assert.Equal("hello", result.Message);
    }

    [Fact]
    public async Task Failing_echo_rearms_per_retry_then_settles_failed()
    {
        var enqueued = await Jobs.EnqueueAsync(new Echo("boom"), ct: Ct);

        // learn: 3. Assert each retry, then the final failure
        // Attempts 1 and 2 throw; with retries left the job re-arms to Ready (due now), not Failed.
        Assert.Equal(ActaRunStatus.Rearmed, await Host.RunOnceAsync(enqueued, Ct));
        Assert.Equal(ActaRunStatus.Rearmed, await Host.RunOnceAsync(enqueued, Ct));

        // Attempt 3 spends the MaxAttempts budget: terminal Failed.
        Assert.Equal(ActaRunStatus.Failed, await Host.RunOnceAsync(enqueued, Ct));
        // learn: end
        Assert.Equal(JobStatusCode.Failed, (await enqueued.GetStatusAsync(Ct))?.Status);
    }
}
```

### ActaHostFixture.cs

```csharp
// xUnit class fixture: one ActaTestHost (and its schema migration) per test class, not per [Fact].

using Acta.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Acta.Concepts.Testing;

public sealed class ActaHostFixture : IAsyncLifetime
{
    public IActaTestHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();

        // learn: 1. Start one test host for the test class
        // The fixture starts the host, and migrates its schema, once per test class rather than once per test.
        Host = await ActaTestHost.StartAsync(
            (j, schema) =>
            {
                j.UseLocalDatabase(configuration, schema);
                j.Run<TestingJobs>("testing-jobs");
            }
        );
        // learn: end
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is not null)
        {
            await Host.DisposeAsync();
        }
    }
}

// Base for Acta tests: exposes the host, jobs, and the test cancellation token.
public abstract class ActaTestBase(ActaHostFixture acta) : IClassFixture<ActaHostFixture>
{
    protected IActaTestHost Host => acta.Host;

    protected IJobs Jobs => acta.Host.Jobs;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;
}
```

### EchoJob.cs

```csharp
namespace Acta.Concepts.Testing;

public sealed record Echo(string Message);

public sealed record EchoResult(string Message);

// "boom" always throws: retry path is Rearmed, Rearmed, then Failed once MaxAttempts spends. 0s
// backoff keeps each retry due now, so one attempt == one RunOnceAsync tick.
public static class EchoJob
{
    [Job("echo", MaxAttempts = 3, Backoff = "0s")]
    public static EchoResult Handle(Echo input) =>
        input.Message == "boom" ? throw new InvalidOperationException("echo cannot say boom") : new(input.Message);
}
```

