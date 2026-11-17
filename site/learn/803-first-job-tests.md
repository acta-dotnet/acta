# How to test the first-job guide's app

The first-job guide's test verbatim: the real runtime on a throwaway SQLite file, driven one tick at a time, with a retry made due at once.

- Section: Test without sleeps or polling, step 3
- Program: `concepts/800-testing/803-first-job-tests` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Runs as a test. This program is a test project: it passes or fails with the test suite.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/803-first-job-tests
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Start the real runtime on a throwaway database

The real runtime on a throwaway SQLite file; no claim loop runs, so nothing executes until a test drives a tick.

The lines that matter:

```csharp
_host = await ActaTestHost.StartAsync(
    (j, _) =>
    {
        j.UseSqlite(sqlite =>
        {
            sqlite.ConnectionString = $"Data Source={_database}";
            sqlite.MigrateOnStartup = true;
        });
        j.Run<ShippingJobs>("shipping");
    },
    new ActaTestHostOptions { ConfigureServices = services => services.AddSingleton(_carrier).AddSingleton(_printer) },
    Ct
);
```

In `ShippingTests.cs` (excerpt):

```csharp
private IActaTestHost _host = null!;

private static CancellationToken Ct => TestContext.Current.CancellationToken;

public async ValueTask InitializeAsync()
{
    _host = await ActaTestHost.StartAsync(
        (j, _) =>
        {
            j.UseSqlite(sqlite =>
            {
                sqlite.ConnectionString = $"Data Source={_database}";
                sqlite.MigrateOnStartup = true;
            });
            j.Run<ShippingJobs>("shipping");
        },
        new ActaTestHostOptions { ConfigureServices = services => services.AddSingleton(_carrier).AddSingleton(_printer) },
        Ct
    );
}

public async ValueTask DisposeAsync()
{
    await _host.DisposeAsync();
    SqliteConnection.ClearAllPools(); // the pool keeps the file open; release it before deleting
```

## 2. Drive one tick and read the typed result

The lines that matter:

```csharp
Assert.Equal(ActaRunStatus.Completed, await _host.RunOnceAsync(job, Ct));

var label = await job.GetResultAsync(Ct);
Assert.Equal("AP00001042", label.TrackingNumber);
```

In `ShippingTests.cs` (excerpt):

```csharp
[Fact]
public async Task A_shipped_order_has_a_typed_label()
{
    var job = await _host.Jobs.EnqueueAsync(ShippingJobs.ShipOrder(new ShipOrder(1042)), ct: Ct);
    Assert.Equal(JobStatusCode.Ready, (await job.GetStatusAsync(Ct))?.Status);

    Assert.Equal(ActaRunStatus.Completed, await _host.RunOnceAsync(job, Ct));

    var label = await job.GetResultAsync(Ct);
    Assert.Equal("AP00001042", label.TrackingNumber);
}

[Fact]
public async Task A_carrier_timeout_costs_an_attempt_and_the_retry_ships()
{
    _carrier.TimeoutsLeft = 1;
```

## 3. Make the retry due now instead of waiting for it

The retry is due about a minute out under the default backoff; the test makes it due now.

The lines that matter:

```csharp
await _host.ForceJobDueAsync(job, Ct);
Assert.Equal(ActaRunStatus.Completed, await _host.RunOnceAsync(job, Ct));
```

In `ShippingTests.cs` (excerpt):

```csharp
{
    _carrier.TimeoutsLeft = 1;
    var job = await _host.Jobs.EnqueueAsync(ShippingJobs.ShipOrder(new ShipOrder(1044)), ct: Ct);

    Assert.Equal(ActaRunStatus.Rearmed, await _host.RunOnceAsync(job, Ct));

    await _host.ForceJobDueAsync(job, Ct);
    Assert.Equal(ActaRunStatus.Completed, await _host.RunOnceAsync(job, Ct));
    Assert.Equal("AP00001044", (await job.GetResultAsync(Ct)).TrackingNumber);
}

[Fact]
public async Task A_one_attempt_job_fails_for_good_and_explains_itself()
{
```

## 4. Run it

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet test concepts/800-testing/803-first-job-tests
```

Runs as a test. This program is a test project: it passes or fails with the test suite.

## Next

- [How to test a recurring synchronization job](/learn/804-continuous-synchronization-tests.md)

## More

- [The whole program](/learn/803-first-job-tests.program.md): every file as compiled, 117 lines.
