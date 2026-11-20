# How to test a job that waits for a signal

Each `RunOnceAsync` drives exactly one attempt, so the suspend on a signal and the resume after its raise are two explicit ticks, and the test asserts that the step before the wait ran once across the replay. Run it with `dotnet test learn/800-testing/802-testing-durable-jobs`.

- Section: Test without sleeps or polling, step 2
- Program: `learn/800-testing/802-testing-durable-jobs` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Runs as a test. This program is a test project: it passes or fails with the test suite.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/802-testing-durable-jobs
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Drive the job to its wait

The job under test, `process-order`, reserves stock in a step, waits for the signal `approval`, then charges. First tick: reserve runs, then the job suspends waiting for approval (re-armed, not done).

The lines that matter:

```csharp
Assert.Equal(ActaRunStatus.Rearmed, await Host.RunOnceAsync(enqueued, Ct));
Assert.Equal(JobStatusCode.Suspended, (await enqueued.GetStatusAsync(Ct))?.Status);
```

In `OrderTests.cs` (excerpt):

```csharp
[Fact]
public async Task Order_reserves_once_waits_for_approval_then_charges()
{
    var enqueued = await Jobs.EnqueueAsync(new PlaceOrder("o-1", 99m), ct: Ct);

    Assert.Equal(ActaRunStatus.Rearmed, await Host.RunOnceAsync(enqueued, Ct));
    Assert.Equal(JobStatusCode.Suspended, (await enqueued.GetStatusAsync(Ct))?.Status);

    var raise = await Jobs.RaiseSignalAsync(enqueued, "approval", new ApprovalDecision(true, "alice"), Ct);
    Assert.Equal(ControlAction.Applied, raise.Action);

    Assert.Equal(ActaRunStatus.Completed, await Host.RunOnceAsync(enqueued, Ct));
    Assert.Equal(JobStatusCode.Succeeded, (await enqueued.GetStatusAsync(Ct))?.Status);

    var result = await enqueued.GetResultAsync<OrderResult>(Ct);
    Assert.True(result.Charged);
    Assert.Equal(1, Warehouse.ReservationsFor("o-1")); // reserve ran exactly once across the replay
}
```

## 2. Raise the signal from the test

The approval API raises a typed decision; the job moves to Ready.

The lines that matter:

```csharp
var raise = await Jobs.RaiseSignalAsync(enqueued, "approval", new ApprovalDecision(true, "alice"), Ct);
Assert.Equal(ControlAction.Applied, raise.Action);
```

In `OrderTests.cs` (excerpt):

```csharp
[Fact]
public async Task Order_reserves_once_waits_for_approval_then_charges()
{
    var enqueued = await Jobs.EnqueueAsync(new PlaceOrder("o-1", 99m), ct: Ct);

    Assert.Equal(ActaRunStatus.Rearmed, await Host.RunOnceAsync(enqueued, Ct));
    Assert.Equal(JobStatusCode.Suspended, (await enqueued.GetStatusAsync(Ct))?.Status);

    var raise = await Jobs.RaiseSignalAsync(enqueued, "approval", new ApprovalDecision(true, "alice"), Ct);
    Assert.Equal(ControlAction.Applied, raise.Action);

    Assert.Equal(ActaRunStatus.Completed, await Host.RunOnceAsync(enqueued, Ct));
    Assert.Equal(JobStatusCode.Succeeded, (await enqueued.GetStatusAsync(Ct))?.Status);

    var result = await enqueued.GetResultAsync<OrderResult>(Ct);
    Assert.True(result.Charged);
    Assert.Equal(1, Warehouse.ReservationsFor("o-1")); // reserve ran exactly once across the replay
}
```

## 3. Drive it again, and assert that it finished

Second tick: the job replays (reserve is NOT re-run), the signal is Set, charge runs, Done.

The lines that matter:

```csharp
Assert.Equal(ActaRunStatus.Completed, await Host.RunOnceAsync(enqueued, Ct));
Assert.Equal(JobStatusCode.Succeeded, (await enqueued.GetStatusAsync(Ct))?.Status);
```

In `OrderTests.cs` (excerpt):

```csharp
[Fact]
public async Task Order_reserves_once_waits_for_approval_then_charges()
{
    var enqueued = await Jobs.EnqueueAsync(new PlaceOrder("o-1", 99m), ct: Ct);

    Assert.Equal(ActaRunStatus.Rearmed, await Host.RunOnceAsync(enqueued, Ct));
    Assert.Equal(JobStatusCode.Suspended, (await enqueued.GetStatusAsync(Ct))?.Status);

    var raise = await Jobs.RaiseSignalAsync(enqueued, "approval", new ApprovalDecision(true, "alice"), Ct);
    Assert.Equal(ControlAction.Applied, raise.Action);

    Assert.Equal(ActaRunStatus.Completed, await Host.RunOnceAsync(enqueued, Ct));
    Assert.Equal(JobStatusCode.Succeeded, (await enqueued.GetStatusAsync(Ct))?.Status);

    var result = await enqueued.GetResultAsync<OrderResult>(Ct);
    Assert.True(result.Charged);
    Assert.Equal(1, Warehouse.ReservationsFor("o-1")); // reserve ran exactly once across the replay
}
```

## 4. Run it

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet test learn/800-testing/802-testing-durable-jobs
```

Runs as a test. This program is a test project: it passes or fails with the test suite.

## Remember

- `"reserve-stock"` and `"charge"` are the keys to each step's stored result, and `"approval"` names the signal the order waits on. A test that renames one is testing a different job from the one already stored.
- A job that waits for a signal or a timer can stay stored for days. The name in `[Job("process-order")]` and the namespace in `Run<TestingDurableJobs>("testing-durable")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `PlaceOrder` is stored with each job as its input, and `OrderResult` as its result. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `process-order-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to test the first-job guide's app](/learn/803-first-job-tests.md)

## More

- [The whole program](/learn/802-testing-durable-jobs.program.md): every file as compiled, 153 lines.
