// File: ShippingTests.cs
using Acta;
using Acta.Sqlite;
using Acta.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shipping;
using Xunit;

public sealed class ShippingTests : IAsyncLifetime
{
    private readonly LabelPrinter _printer = new();
    private IActaTestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // The real runtime on a throwaway SQLite file, named by the schema the host allocates; no claim
        // loop runs, so nothing executes until a test drives a tick.
        _host = await ActaTestHost.StartAsync(
            (j, schema) =>
            {
                j.UseSqlite(sqlite =>
                {
                    sqlite.ConnectionString = $"Data Source={schema}.db";
                    sqlite.ApplyMigrationsOnStartup = true;
                });
                j.Run<ShippingJobs>("shipping");
            },
            new ActaTestHostOptions { ConfigureServices = services => services.AddSingleton(_printer) },
            Ct
        );
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        SqliteConnection.ClearAllPools(); // the pool keeps the file open; release it before deleting
        File.Delete($"{_host.Schema}.db");
    }

    [Fact]
    public async Task A_shipped_order_has_a_typed_label()
    {
        var job = await _host.Jobs.EnqueueAsync(ShippingJobs.ShipOrder(new ShipOrder(1042)), ct: Ct);
        Assert.Equal(JobStatusCode.Ready, (await job.GetStatusAsync(Ct))?.Status);

        Assert.Equal(ActaRunStatus.Completed, await _host.RunOnceAsync(job, Ct));

        var label = await job.GetResultAsync(Ct);
        Assert.Equal("AP00001042", label!.TrackingNumber);
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
        var job = await _host.Jobs.EnqueueAsync(ShippingJobs.ShipOrder(new ShipOrder(7)), ct: Ct);

        var outcome = await job.WaitAsync(TimeSpan.FromMilliseconds(50), Ct);

        Assert.True(outcome.IsTimedOut);
        Assert.Equal(JobStatusCode.Ready, (await job.GetStatusAsync(Ct))?.Status);
        Assert.Equal(ActaRunStatus.Completed, await _host.RunOnceAsync(job, Ct));
    }

    [Fact]
    public async Task A_queued_job_cancels_and_never_runs()
    {
        var job = await _host.Jobs.ScheduleAsync(ShippingJobs.ShipOrder(new ShipOrder(8)), TimeSpan.FromHours(1), ct: Ct);

        var cancelled = await job.CancelAsync(reasonMessage: "customer withdrew the order", ct: Ct);

        Assert.Equal(ControlAction.Applied, cancelled.Action);
        Assert.Equal(ActaRunStatus.NothingClaimed, await _host.RunOnceAsync(job, Ct));
        Assert.Equal(JobStatusCode.Cancelled, (await job.GetStatusAsync(Ct))?.Status);
    }
}
