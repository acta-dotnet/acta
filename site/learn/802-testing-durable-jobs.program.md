# The whole program: test a job that waits for a signal

The how-to is [/learn/802-testing-durable-jobs.md](/learn/802-testing-durable-jobs.md). This file holds every source file of `learn/800-testing/802-testing-durable-jobs` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### OrderTests.cs

```csharp
// How to test a job that waits for a signal
// Each `RunOnceAsync` drives exactly one attempt, so the suspend on a signal and the resume after its raise are two
// explicit ticks, and the test asserts that the step before the wait ran once across the replay. Run it with
// `dotnet test learn/800-testing/802-testing-durable-jobs`.
using Acta.Testing;
using Xunit;

namespace Acta.Learn.TestingDurable;

public sealed class OrderTests(ActaHostFixture acta) : ActaTestBase(acta)
{
    [Fact]
    public async Task Order_reserves_once_waits_for_approval_then_charges()
    {
        var enqueued = await Jobs.EnqueueAsync(new PlaceOrder("o-1", 99m), ct: Ct);

        // learn: 1. Drive the job to its wait
        // First tick: reserve runs, then the job suspends waiting for approval (re-armed, not done).
        Assert.Equal(ActaRunStatus.Rearmed, await Host.RunOnceAsync(enqueued, Ct));
        Assert.Equal(JobStatusCode.Suspended, (await enqueued.GetStatusAsync(Ct))?.Status);
        // learn: end

        // learn: 2. Raise the signal from the test
        // The approval API raises a typed decision; the job moves to Ready.
        var raise = await Jobs.RaiseSignalAsync(enqueued, "approval", new ApprovalDecision(true, "alice"), Ct);
        Assert.Equal(ControlAction.Applied, raise.Action);
        // learn: end

        // learn: 3. Drive it again, and assert that it finished
        // Second tick: the job replays (reserve is NOT re-run), the signal is Set, charge runs, Done.
        Assert.Equal(ActaRunStatus.Completed, await Host.RunOnceAsync(enqueued, Ct));
        Assert.Equal(JobStatusCode.Succeeded, (await enqueued.GetStatusAsync(Ct))?.Status);
        // learn: end

        var result = await enqueued.GetResultAsync<OrderResult>(Ct);
        Assert.True(result.Charged);
        Assert.Equal(1, Warehouse.ReservationsFor("o-1")); // reserve ran exactly once across the replay
    }

    [Fact]
    public async Task Rejected_order_completes_without_charging()
    {
        var enqueued = await Jobs.EnqueueAsync(new PlaceOrder("o-2", 50m), ct: Ct);

        Assert.Equal(ActaRunStatus.Rearmed, await Host.RunOnceAsync(enqueued, Ct));

        await Jobs.RaiseSignalAsync(enqueued, "approval", new ApprovalDecision(false, "bob"), Ct);

        Assert.Equal(ActaRunStatus.Completed, await Host.RunOnceAsync(enqueued, Ct));
        var result = await enqueued.GetResultAsync<OrderResult>(Ct);
        Assert.False(result.Charged);
    }
}
```

### ActaHostFixture.cs

```csharp
// xUnit class fixture: one ActaTestHost per test class, like 801, for a durable multi-step job.

using Acta.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Learn.TestingDurable;

public sealed class ActaHostFixture : IAsyncLifetime
{
    public IActaTestHost Host { get; private set; } = null!;

    // The job's dependency, registered as a service rather than kept in a static field the tests would share.
    public Warehouse Warehouse { get; } = new();

    public async ValueTask InitializeAsync()
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();

        Host = await ActaTestHost.StartAsync(
            (j, schema) =>
            {
                j.UseLocalDatabase(configuration, schema);
                j.Run<TestingDurableJobs>("testing-durable");
            },
            new ActaTestHostOptions { ConfigureServices = services => services.AddSingleton(Warehouse) }
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is not null)
        {
            await Host.DisposeAsync();
        }
    }
}

public abstract class ActaTestBase(ActaHostFixture acta) : IClassFixture<ActaHostFixture>
{
    protected IActaTestHost Host => acta.Host;

    protected IJobs Jobs => acta.Host.Jobs;

    protected Warehouse Warehouse => acta.Warehouse;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;
}
```

### OrderJob.cs

```csharp
// Durable multi-step job under test: reserve stock, wait for an approval signal, then charge.

using System.Collections.Concurrent;

namespace Acta.Learn.TestingDurable;

public sealed record PlaceOrder(string OrderId, decimal Amount);

public sealed record ApprovalDecision(bool Approved, string By);

public sealed record OrderResult(string OrderId, bool Charged);

// The stock system the job reserves against. A test registers its own instance and reads how many times an order's
// reservation ran, so the run-once proof holds per order even while tests share the host.
public sealed class Warehouse
{
    private readonly ConcurrentDictionary<string, int> _reservations = new();

    public void Reserve(string orderId) => _reservations.AddOrUpdate(orderId, 1, (_, count) => count + 1);

    public int ReservationsFor(string orderId) => _reservations.GetValueOrDefault(orderId);
}

public sealed class OrderJob(Warehouse warehouse)
{
    [Job("process-order")]
    public async Task<OrderResult> Handle(PlaceOrder order, JobContext context, CancellationToken ct)
    {
        await context.RunStepAsync(
            "reserve-stock",
            _ =>
            {
                warehouse.Reserve(order.OrderId);
                return Task.CompletedTask;
            },
            ct
        );

        // Suspend until the signal arrives; on resume the job replays from the top but the recorded
        // reserve step is NOT re-run.
        var decision = await context.WaitSignalAsync<ApprovalDecision>("approval", ct);
        if (!decision!.Approved)
        {
            return new OrderResult(order.OrderId, Charged: false);
        }

        await context.RunStepAsync("charge", _ => Task.CompletedTask, ct);
        return new OrderResult(order.OrderId, Charged: true);
    }
}
```

