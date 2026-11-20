# How to test a handler with the test host

`Acta.Testing` runs the real runtime on a real database with no background loop: `RunOnceAsync` decides when a job executes, so a test drives one attempt at a time. Run it with `dotnet test learn/800-testing/801-testing-jobs`.

- Section: Test without sleeps or polling, step 1
- Program: `learn/800-testing/801-testing-jobs` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Runs as a test. This program is a test project: it passes or fails with the test suite.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/801-testing-jobs
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Start one test host for the test class

The fixture starts the host, and migrates its schema, once per test class rather than once per test.

The lines that matter:

```csharp
Host = await ActaTestHost.StartAsync(
    (j, schema) =>
    {
        j.UseLocalDatabase(configuration, schema);
        j.Run<TestingJobs>("testing-jobs");
    }
);
```

In `ActaHostFixture.cs` (excerpt):

```csharp
public IActaTestHost Host { get; private set; } = null!;

public async ValueTask InitializeAsync()
{
    var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();

    Host = await ActaTestHost.StartAsync(
        (j, schema) =>
        {
            j.UseLocalDatabase(configuration, schema);
            j.Run<TestingJobs>("testing-jobs");
        }
    );
}
```

## 2. Drive one attempt and assert what became of it

`RunOnceAsync` runs one attempt of that job and answers its outcome.

The lines that matter:

```csharp
var outcome = await Host.RunOnceAsync(enqueued, Ct);

Assert.Equal(ActaRunStatus.Completed, outcome);
```

In `EchoJobTests.cs` (excerpt):

```csharp
[Fact]
public async Task Echo_runs_when_driven_and_returns_the_message()
{
    var enqueued = await Jobs.EnqueueAsync(new Echo("hello"), ct: Ct);

    // Nothing executes until the test drives a tick.
    Assert.Equal(JobStatusCode.Ready, (await enqueued.GetStatusAsync(Ct))?.Status);

    var outcome = await Host.RunOnceAsync(enqueued, Ct);

    Assert.Equal(ActaRunStatus.Completed, outcome);
    Assert.Equal(JobStatusCode.Succeeded, (await enqueued.GetStatusAsync(Ct))?.Status);
    var result = await enqueued.GetResultAsync<EchoResult>(Ct);
    Assert.Equal("hello", result.Message);
}
```

## 3. Assert each retry, then the final failure

Attempts 1 and 2 throw; with retries left the job re-arms to Ready, not Failed. The job declares `MaxAttempts = 3, Backoff = "0s"`, so each retry is due at once; under the default backoff a test makes it due with `ForceJobDueAsync`, as how-to 803 does.

The lines that matter:

```csharp
Assert.Equal(ActaRunStatus.Rearmed, await Host.RunOnceAsync(enqueued, Ct));
Assert.Equal(ActaRunStatus.Rearmed, await Host.RunOnceAsync(enqueued, Ct));

// Attempt 3 spends the MaxAttempts budget: terminal Failed.
Assert.Equal(ActaRunStatus.Failed, await Host.RunOnceAsync(enqueued, Ct));
```

In `EchoJobTests.cs` (excerpt):

```csharp
[Fact]
public async Task Failing_echo_rearms_per_retry_then_settles_failed()
{
    var enqueued = await Jobs.EnqueueAsync(new Echo("boom"), ct: Ct);

    Assert.Equal(ActaRunStatus.Rearmed, await Host.RunOnceAsync(enqueued, Ct));
    Assert.Equal(ActaRunStatus.Rearmed, await Host.RunOnceAsync(enqueued, Ct));

    // Attempt 3 spends the MaxAttempts budget: terminal Failed.
    Assert.Equal(ActaRunStatus.Failed, await Host.RunOnceAsync(enqueued, Ct));
    Assert.Equal(JobStatusCode.Failed, (await enqueued.GetStatusAsync(Ct))?.Status);
}
```

## 4. Run it

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet test learn/800-testing/801-testing-jobs
```

Runs as a test. This program is a test project: it passes or fails with the test suite.

## Remember

- The name in `[Job("echo")]` and the namespace in `Run<TestingJobs>("testing-jobs")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `Echo` is stored with each job as its input, and `EchoResult` as its result. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `echo-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to test a job that waits for a signal](/learn/802-testing-durable-jobs.md)

## More

- [The whole program](/learn/801-testing-jobs.program.md): every file as compiled, 107 lines.
