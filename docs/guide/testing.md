# Testing custom jobs

Test `[Job]` handlers end-to-end with `Acta.Testing`: a real database, the real runtime, and a
deterministic single-step drive. No polling loops, no sleeps, no background worker racing assertions: a tick is one claim plus one handler run, so these integration tests finish in tens of milliseconds.

Runnable version: [`concepts/800-testing/801-testing-jobs`](../../concepts/800-testing/801-testing-jobs/)
(`dotnet test concepts/800-testing/801-testing-jobs`).

The package is test-framework-agnostic (xUnit, NUnit, MSTest, TUnit). It exposes three types:

| Type | Role |
|------|------|
| `ActaTestHost.StartAsync(...)` | Stands up the full Acta runtime on a throwaway schema and returns the host |
| `IActaTestHost` | `Jobs` (the public `IJobs` surface), `Services`, `Schema`, and `RunOnceAsync` |
| `ActaRunOutcome` | What one drive tick did: `NothingClaimed`, `Completed`, `Failed`, `Rearmed` |

## The model

`ActaTestHost.StartAsync` builds the same DI container production uses, applies migrations to a
unique throwaway schema (default `acta_test_{12-hex}`), and registers your manifest's catalog
(namespace, definitions, worker row). It does not start the background claim loop; nothing executes
until you drive it.

Drive execution one tick at a time:

```csharp
var outcome = await host.RunOnceAsync(enqueued, ct);   // claim + execute exactly this job
var outcome = await host.RunOnceAsync("users", ct);          // claim + execute at most one Ready job in the namespace
```

Enqueue, drive, assert, in that order, deterministically. Assertions go through the same public
`IJobs` read surface production uses (`GetAsync`, `GetStatusAsync`, `GetResultAsync<T>`).

## Dependency

The test project references the testing library plus the same provider the application uses. While
the preview is source-only, use project references from this repository; when packages are
published, the same IDs become `PackageReference`s.

```xml
<ItemGroup>
  <ProjectReference Include="src/Acta.Testing/Acta.Testing.csproj" />
  <ProjectReference Include="src/Acta.Postgres/Acta.Postgres.csproj" />
</ItemGroup>
```

A complete runnable version of this setup is concept
[`801-testing-jobs`](../../concepts/800-testing/801-testing-jobs/).
The database is whatever the test environment provides (local container, CI service, or shared dev
server). Each host targets its own schema, so one database serves many parallel tests.

## First test

The handler under test is the quickstart's `send-welcome-email` ([`quickstart.md`](../quickstart.md)).
Its `EmailService` dependency is replaced with a fake through `ActaTestHostOptions.ConfigureServices`.

```csharp
using Acta;
using Microsoft.Extensions.DependencyInjection;
using Users.Jobs;
using Xunit;

public sealed class SendWelcomeEmailTests : IAsyncLifetime
{
    private IActaTestHost _host = null!;
    private FakeEmailService _email = null!;

    public async ValueTask InitializeAsync()
    {
        _email = new FakeEmailService();
        _host = await ActaTestHost.StartAsync(
            (j, schema) =>
            {
                j.UsePostgres(opts =>
                {
                    opts.ConnectionString = Environment.GetEnvironmentVariable("ACTA_TEST_PG")!;
                    opts.Schema = schema;                  // the throwaway schema the host allocated
                    opts.ApplyMigrationsOnStartup = true;  // migrate it
                });
                j.Run<UsersJobs>(namespaceName: "users", ownerTeam: "growth");
            },
            new ActaTestHostOptions
            {
                ConfigureServices = s =>
                {
                    s.AddSingleton<EmailService>(_email);
                    s.AddScoped<UserRegistrationService>();
                },
            });
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Welcome_email_job_sends_and_completes()
    {
        var userId = Guid.CreateVersion7();
        var enqueued = await _host.Jobs.EnqueueAsync(new SendWelcomeEmail(userId, "ada@example.com", "Ada"));

        var outcome = await _host.RunOnceAsync(enqueued);

        Assert.Equal(ActaRunOutcome.Completed, outcome);
        Assert.Equal(JobStatusCode.Succeeded, await _host.Jobs.GetStatusAsync(enqueued));
        var sent = Assert.Single(_email.Sent);
        Assert.Equal(userId, sent.UserId);
    }
}
```

- The callback receives the schema name. Pass it to the provider's `opts.Schema` and set
  `ApplyMigrationsOnStartup = true`; the host owns a fresh schema per test class.
- `Run<TManifest>(...)` makes it a worker host. Without it the host is enqueue-only (for testing
  enqueue-side code such as HTTP handlers); `RunOnceAsync` then throws.
- `GetStatusAsync(enqueued)` works because `JobEnqueueOutcome` converts implicitly to `JobLookup`;
  `JobLookup.ById(...)` / `JobLookup.ByDeduplicationKey(...)` are the explicit forms.
- `RunOnceAsync(enqueued)` is sugar for `RunOnceAsync(enqueued.JobId)`. The by-id drive needs a
  single-worker host and retries a transiently-missed claim for a few seconds (the claim uses
  skip-locked reads). Multi-worker hosts disambiguate with `RunOnceAsync(namespace)`.

## Reading the outcome

`ActaRunOutcome` tells you what the tick did to the row:

| Outcome | Meaning | Row status afterwards |
|---------|---------|----------------------|
| `Completed` | The handler finished (or the run was cancelled) | `Done` / `Cancelled` |
| `Rearmed` | The job re-armed for a later claim: the handler rescheduled, slept, suspended on a signal, or threw with retry attempts remaining | `Ready` (forward-dated `NextRunAtUtc`) or `Suspended` |
| `Failed` | The handler threw and the row settled terminally (attempts exhausted, or an exception classified as non-retryable: `NotImplementedException` / `NotSupportedException`) | `Failed` |
| `NothingClaimed` | No claimable row this tick (not enqueued, not yet due, or already claimed) | unchanged |

A first failure of a job with default policy (`MaxAttempts = 15`) returns `Rearmed`, not `Failed`:
the row is back at `Ready` with backoff applied.

## Testing the retry path

After a failed attempt, `NextRunAtUtc` is pushed forward by the backoff policy (default initial
delay `1m`), so an immediate second `RunOnceAsync` returns `NothingClaimed` (the row is not due).
To walk the retry path deterministically, give the test job a zero backoff:

```csharp
public static class FlakyJobs
{
    public static int Attempts;

    [Job("flaky-step", MaxAttempts = 2, Backoff = "0s")]
    public static Task Handle(FlakyStep input)
    {
        if (Interlocked.Increment(ref Attempts) == 1)
        {
            throw new InvalidOperationException("first attempt fails");
        }
        return Task.CompletedTask;
    }
}
```

```csharp
[Fact]
public async Task Retries_once_then_completes()
{
    FlakyJobs.Attempts = 0;
    var enqueued = await _host.Jobs.EnqueueAsync(new FlakyStep());

    Assert.Equal(ActaRunOutcome.Rearmed, await _host.RunOnceAsync(enqueued));   // attempt 1 throws
    Assert.Equal(ActaRunOutcome.Completed, await _host.RunOnceAsync(enqueued)); // attempt 2 succeeds
    Assert.Equal(2, FlakyJobs.Attempts);
}
```

With `MaxAttempts = 2`, making both attempts throw turns the second tick into
`ActaRunOutcome.Failed` and the row terminal.

## Testing orchestration: signals

A handler that waits on a durable signal parks the job; the test releases it and drives the next
tick:

```csharp
public sealed record ApproveExpense(long ExpenseId);

public static class ExpenseJobs
{
    [Job("approve-expense")]
    public static async Task Handle(ApproveExpense input, JobContext ctx, CancellationToken ct)
    {
        var approved = await ctx.WaitSignalAsync<bool>("approval", ct);
        if (!approved)
        {
            await ctx.FailAsync("rejected", ct);
        }
    }
}
```

```csharp
[Fact]
public async Task Waits_for_the_approval_signal_then_completes()
{
    var enqueued = await _host.Jobs.EnqueueAsync(new ApproveExpense(42));

    // Tick 1: the handler reaches WaitSignalAsync and parks durably.
    Assert.Equal(ActaRunOutcome.Rearmed, await _host.RunOnceAsync(enqueued));
    Assert.Equal(JobStatusCode.Suspended, await _host.Jobs.GetStatusAsync(enqueued));

    // Release: the signal moves the job back to Ready, due now.
    await _host.Jobs.RaiseSignalAsync(enqueued, "approval", true);

    // Tick 2: the handler resumes past the wait and completes.
    Assert.Equal(ActaRunOutcome.Completed, await _host.RunOnceAsync(enqueued));
    Assert.Equal(JobStatusCode.Succeeded, await _host.Jobs.GetStatusAsync(enqueued));
}
```

The same two-tick pattern covers every re-arming primitive: `ctx.SleepAsync` / `RescheduleAsync`
(tick again once due), child jobs (`StartChildAsync` + drive the child's id), and steps
(`RunStepAsync` checkpoints replay across retry ticks).

## Asserting results

A handler that returns a value persists it; read it back typed:

```csharp
var enqueued = await _host.Jobs.EnqueueAsync(new AddNumbers(2, 3));
await _host.RunOnceAsync(enqueued);

var result = await _host.Jobs.GetResultAsync<AddNumbersResult>(enqueued);
Assert.Equal(5, result!.Sum);
```

For everything `IJobs` does not project (audit timeline, tags, lease rows), resolve what you need
from `host.Services` or query the schema directly: `host.Schema` names it, and
[`data-model.md`](../reference/data-model.md) documents every table.

## Host lifecycle and cost

- `IAsyncLifetime` on a test class runs per fact. xUnit creates a fresh test-class instance for
  every test, so the first-test shape above pays one host (and one full M001 migration: tables +
  routines) per fact: maximal isolation, slowest. To pay it once per class, move the host into an
  `IClassFixture<>` and inherit a small test base;
  [`concepts/800-testing/801-testing-jobs`](../../concepts/800-testing/801-testing-jobs/) shows that shape (`ActaHostFixture` +
  `ActaTestBase`). Shared fakes then need per-test-scoped state since the class's facts share one
  instance.
- Disposal does not drop the schema. Throwaway schemas accumulate in the test database; recycle the
  database periodically, or pin `ActaTestHostOptions.Schema` to a fixed name and reset it yourself.
- Pinned schemas share state. With `options.Schema = "acta_it"` across classes, use unique
  deduplication keys and namespaces per test or assertions will see each other's rows.
- Fakes and clocks go in `ConfigureServices`. It runs after `UseActa`, so it can replace
  anything the runtime registered as well as register your handler classes' dependencies.
- At-least-once still applies. The test host runs the production pipeline; a handler that is not
  idempotent fails in tests the same way it would in production.
