# The whole program: test the first-job guide's app

The how-to is [/learn/803-first-job-tests.md](/learn/803-first-job-tests.md). This file holds every source file of `learn/800-testing/803-first-job-tests` as it is compiled.

## The whole program

### ShippingTests.cs

```csharp
// File: ShippingTests.cs
// How to test the first-job guide's app
// The test of the first-job app ([026-first-job](../../000-fundamentals/026-first-job/)), as the first-job guide
// shows it: the real runtime on a throwaway SQLite file, driven one tick at a time, with a retry made due at once.
using Acta;
using Acta.Sqlite;
using Acta.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shipping;
using Xunit;

public sealed class ShippingTests : IAsyncLifetime
{
    private readonly Carrier _carrier = new();
    private readonly LabelPrinter _printer = new();
    private readonly string _database = $"shipping-{Guid.NewGuid():N}.db";
    private IActaTestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // learn: 1. Start the real runtime on a throwaway database
        // The real runtime on a throwaway SQLite file; no claim loop runs, so nothing executes until a test
        // drives a tick.
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
        // learn: end
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        SqliteConnection.ClearAllPools(); // the pool keeps the file open; release it before deleting
        File.Delete(_database);
    }

    [Fact]
    public async Task A_shipped_order_has_a_typed_label()
    {
        var job = await _host.Jobs.EnqueueAsync(ShippingJobs.ShipOrder(new ShipOrder(1042)), ct: Ct);
        Assert.Equal(JobStatusCode.Ready, (await job.GetStatusAsync(Ct))?.Status);

        // learn: 2. Drive one tick and read the typed result
        Assert.Equal(ActaRunStatus.Completed, await _host.RunOnceAsync(job, Ct));

        var label = await job.GetResultAsync(Ct);
        Assert.Equal("AP00001042", label.TrackingNumber);
        // learn: end
    }

    [Fact]
    public async Task A_carrier_timeout_costs_an_attempt_and_the_retry_ships()
    {
        _carrier.TimeoutsLeft = 1;
        var job = await _host.Jobs.EnqueueAsync(ShippingJobs.ShipOrder(new ShipOrder(1044)), ct: Ct);

        Assert.Equal(ActaRunStatus.Rearmed, await _host.RunOnceAsync(job, Ct));

        // learn: 3. Make the retry due now instead of waiting for it
        // The first booking times out (`TimeoutsLeft = 1`), so the first tick fails and re-arms. The retry is due about a
        // minute out under the default backoff; the test makes it due now.
        await _host.ForceJobDueAsync(job, Ct);
        Assert.Equal(ActaRunStatus.Completed, await _host.RunOnceAsync(job, Ct));
        // learn: end
        Assert.Equal("AP00001044", (await job.GetResultAsync(Ct)).TrackingNumber);
    }

    [Fact]
    public async Task A_one_attempt_job_fails_for_good_and_explains_itself()
    {
        _printer.Online = false;
        var job = await _host.Jobs.EnqueueAsync(new PrintLabel("AP00001042"), ct: Ct);

        Assert.Equal(ActaRunStatus.Failed, await _host.RunOnceAsync(job, Ct));

        var why = await job.ExplainAsync(Ct);
        Assert.NotNull(why);
        Assert.Equal(JobStatusCode.Failed, why.Status);
        Assert.Contains("Printer offline", why.Reason ?? "");
    }

    [Fact]
    public async Task A_wait_timeout_ends_the_wait_and_not_the_job()
    {
        var job = await _host.Jobs.EnqueueAsync(new ShipOrder(7), ct: Ct);

        var outcome = await job.WaitAsync(TimeSpan.FromMilliseconds(50), Ct);

        Assert.True(outcome.TimedOut);
        Assert.Equal(JobStatusCode.Ready, (await job.GetStatusAsync(Ct))?.Status);
        Assert.Equal(ActaRunStatus.Completed, await _host.RunOnceAsync(job, Ct));
    }

    [Fact]
    public async Task A_queued_job_cancels_and_never_runs()
    {
        var job = await _host.Jobs.ScheduleAsync(new ShipOrder(8), TimeSpan.FromHours(1), ct: Ct);

        var cancelled = await job.CancelAsync(reasonMessage: "customer withdrew the order", ct: Ct);

        Assert.Equal(ControlAction.Applied, cancelled.Action);
        Assert.Equal(ActaRunStatus.NothingClaimed, await _host.RunOnceAsync(job, Ct));
        Assert.Equal(JobStatusCode.Cancelled, (await job.GetStatusAsync(Ct))?.Status);
    }
}
```

