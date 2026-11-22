# How to integrate Acta with Orleans

You run an app whose users each have a wallet in EUR. The wallet is a grain: the live object, with an address, its state in memory, answering one call at a time. Topping it up is a job: recorded step by step in the database, retried when it fails, and able to wait for days. The grain enqueues the job and returns, and the job's handler calls the grain back as its last step.

- Section: Deploy and scale, step 5
- Program: `learn/700-topology-and-deployment/707-orleans-integration` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/707-orleans-integration
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Run the silo and the worker in one application

The silo hosts the grains and the Acta worker runs the jobs. In one application they share its services, so a grain can take IJobs and a handler can take IGrainFactory.

The lines that matter:

```csharp
builder.UseOrleans(silo =>
{
    silo.UseLocalhostClustering();
    silo.AddMemoryGrainStorageAsDefault();
});
builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<WalletsJobs>("wallets");
});
```

In `orleans-integration.cs` (excerpt):

```csharp
var builder = Host.CreateApplicationBuilder(args);
var lab = new LearnLab(builder.Configuration, args);
builder.Logging.AddFilter("Acta", LogLevel.Error);
builder.Logging.AddFilter("Orleans", LogLevel.Error);

builder.UseOrleans(silo =>
{
    silo.UseLocalhostClustering();
    silo.AddMemoryGrainStorageAsDefault();
});
builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<WalletsJobs>("wallets");
});

using var host = builder.Build();
await host.StartAsync();
```

## 2. Enqueue from the grain and return

The grain hands the top-up to Acta and stays free for its next call, the job's own call back included. The idempotency key makes a repeated TopUpAsync find the same job: the grain keeps no record that it already asked. It answers with the job's ref as a Guid, which a grain call carries and IJobs takes as it is.

The lines that matter:

```csharp
public async Task<Guid> TopUpAsync(string topUpId, decimal amount)
{
    var job = await jobs.EnqueueAsync(new TopUpWallet(this.GetPrimaryKeyLong(), topUpId, amount), o => o.IdempotencyKey(topUpId));
    return job.JobRef;
}
```

In `orleans-integration.cs` (excerpt):

```csharp
public sealed class WalletGrain([PersistentState("wallet")] IPersistentState<WalletState> wallet, IJobs jobs) : Grain, IWalletGrain
{
    public async Task<Guid> TopUpAsync(string topUpId, decimal amount)
    {
        var job = await jobs.EnqueueAsync(new TopUpWallet(this.GetPrimaryKeyLong(), topUpId, amount), o => o.IdempotencyKey(topUpId));
        return job.JobRef;
    }

    public async Task AddToBalanceAsync(string topUpId, decimal amount)
    {
        if (wallet.State.TopUps.Add(topUpId))
        {
            wallet.State.Balance += amount;
            await wallet.WriteStateAsync();
        }
    }

    public Task<decimal> GetBalanceAsync() => Task.FromResult(wallet.State.Balance);
}
```

## 3. Let the job retry by itself

The receipt's mail server is down on the first attempt. Nobody has to call again: the job runs once more after its backoff, and the attempt and its error are on the job's record.

The lines that matter:

```csharp
[Job("top-up-wallet", MaxAttempts = 3, Backoff = "1s")]
```

In `orleans-integration.cs` (excerpt):

```csharp
public sealed class TopUpWalletJob(IGrainFactory grains)
{
    [Job("top-up-wallet", MaxAttempts = 3, Backoff = "1s")]
    public async Task Handle(TopUpWallet input, JobContext context, CancellationToken ct)
    {
        await context.RunStepAsync("request-payment", _ => PaymentProvider.RequestAsync(input.Amount), ct);
        await context.WaitSignalAsync("payment-confirmed", ct);
        var wallet = grains.GetGrain<IWalletGrain>(input.WalletId);
        await context.RunStepAsync("add-to-balance", _ => wallet.AddToBalanceAsync(input.TopUpId, input.Amount), ct);
        await context.RunStepAsync("send-receipt", _ => Receipts.SendAsync(input.Amount, context.ExecutionNumber), ct);
    }
}
```

## 4. Wait for the payment and call the grain back

The wait is a row in the database, not a call held open, so it can last days. Once a step is recorded, a later run reuses it. When the receipt fails and the job runs again, the payment is not requested again, the job does not wait again, and the grain is not told again.

The lines that matter:

```csharp
await context.RunStepAsync("request-payment", _ => PaymentProvider.RequestAsync(input.Amount), ct);
await context.WaitSignalAsync("payment-confirmed", ct);
var wallet = grains.GetGrain<IWalletGrain>(input.WalletId);
await context.RunStepAsync("add-to-balance", _ => wallet.AddToBalanceAsync(input.TopUpId, input.Amount), ct);
await context.RunStepAsync("send-receipt", _ => Receipts.SendAsync(input.Amount, context.ExecutionNumber), ct);
```

In `orleans-integration.cs` (excerpt):

```csharp
public sealed class TopUpWalletJob(IGrainFactory grains)
{
    [Job("top-up-wallet", MaxAttempts = 3, Backoff = "1s")]
    public async Task Handle(TopUpWallet input, JobContext context, CancellationToken ct)
    {
        await context.RunStepAsync("request-payment", _ => PaymentProvider.RequestAsync(input.Amount), ct);
        await context.WaitSignalAsync("payment-confirmed", ct);
        var wallet = grains.GetGrain<IWalletGrain>(input.WalletId);
        await context.RunStepAsync("add-to-balance", _ => wallet.AddToBalanceAsync(input.TopUpId, input.Amount), ct);
        await context.RunStepAsync("send-receipt", _ => Receipts.SendAsync(input.Amount, context.ExecutionNumber), ct);
    }
}
```

## 5. Raise the job's signal when the payment provider confirms

A job's ref names it everywhere: one word, unique across namespaces, so any code that has IJobs reaches the job with it. The app gave the ref to the payment provider as its reference, and here the program stands in for the provider's confirmation, which carries it back.

The lines that matter:

```csharp
var jobs = host.Services.GetRequiredService<IJobs>();
await jobs.RaiseSignalAsync(topUp, "payment-confirmed");
```

In `orleans-integration.cs` (excerpt):

```csharp
// The top-up is not finished, and the grain is free: it answers from the state it holds in memory.
Console.WriteLine($"balance before the payment was confirmed: {await wallet.GetBalanceAsync()} EUR");

var jobs = host.Services.GetRequiredService<IJobs>();
await jobs.RaiseSignalAsync(topUp, "payment-confirmed");

var done = await jobs.WaitAsync(topUp);
done.ThrowIfFailed();
Console.WriteLine($"balance after the payment was confirmed: {await wallet.GetBalanceAsync()} EUR");
```

## 6. Make the call a job sends safe to receive twice

The grain owns its state and saves it when it chooses. The call can arrive again if the job's step was not yet recorded, so the wallet adds a top-up once, by its id.

The lines that matter:

```csharp
public async Task AddToBalanceAsync(string topUpId, decimal amount)
{
    if (wallet.State.TopUps.Add(topUpId))
    {
        wallet.State.Balance += amount;
        await wallet.WriteStateAsync();
    }
}
```

In `orleans-integration.cs` (excerpt):

```csharp
public sealed class WalletGrain([PersistentState("wallet")] IPersistentState<WalletState> wallet, IJobs jobs) : Grain, IWalletGrain
{
    public async Task<Guid> TopUpAsync(string topUpId, decimal amount)
    {
        var job = await jobs.EnqueueAsync(new TopUpWallet(this.GetPrimaryKeyLong(), topUpId, amount), o => o.IdempotencyKey(topUpId));
        return job.JobRef;
    }

    public async Task AddToBalanceAsync(string topUpId, decimal amount)
    {
        if (wallet.State.TopUps.Add(topUpId))
        {
            wallet.State.Balance += amount;
            await wallet.WriteStateAsync();
        }
    }

    public Task<decimal> GetBalanceAsync() => Task.FromResult(wallet.State.Balance);
}
```

## 7. Read what the job recorded with a plain query

The grain holds the balance in memory. What the job did is rows in the database the application already has: one job for the top-up, though it was asked for twice, each step recorded once, and each attempt with its error.

The lines that matter:

```csharp
await lab.ShowAsync(
    "One job for the top-up, though it was asked for twice",
    """
    SELECT definition_name, job_status, execution_number
    FROM jobs_view
    WHERE idempotency_key = @topUpId
    """,
    new { topUpId }
);
await lab.ShowAsync(
    "Each step recorded once, though the job ran twice",
    """
    SELECT step_name, step_status
    FROM steps_view
    WHERE job_id = @jobId
    ORDER BY created_at_utc
    """,
    new { jobId = done.JobId }
);
await lab.ShowAsync(
    "Both attempts, with the first one's error",
    """
    SELECT event, execution_number, event_message
    FROM events_view
    WHERE entity = 'job' AND entity_id = @jobId
    ORDER BY event_id
    """,
    new { jobId = done.JobId }
);
```

In `orleans-integration.cs` (excerpt):

```csharp
var done = await jobs.WaitAsync(topUp);
done.ThrowIfFailed();
Console.WriteLine($"balance after the payment was confirmed: {await wallet.GetBalanceAsync()} EUR");

await lab.ShowAsync(
    "One job for the top-up, though it was asked for twice",
    """
    SELECT definition_name, job_status, execution_number
    FROM jobs_view
    WHERE idempotency_key = @topUpId
    """,
    new { topUpId }
);
await lab.ShowAsync(
    "Each step recorded once, though the job ran twice",
    """
    SELECT step_name, step_status
    FROM steps_view
    WHERE job_id = @jobId
    ORDER BY created_at_utc
    """,
    new { jobId = done.JobId }
);
await lab.ShowAsync(
    "Both attempts, with the first one's error",
    """
    SELECT event, execution_number, event_message
    FROM events_view
    WHERE entity = 'job' AND entity_id = @jobId
    ORDER BY event_id
    """,
    new { jobId = done.JobId }
);

await host.StopAsync();
```

## 8. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/700-topology-and-deployment/707-orleans-integration
```

Its output must contain:

```text
sending the receipt for 100 EUR (attempt 2)
send-receipt
balance before the payment was confirmed: 0 EUR
balance after the payment was confirmed: 100 EUR
top-up-wallet
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- Each does what it is good at. The grain is the live object: it has an address, owns its state, and answers from memory. Acta runs and records the work the grain starts: its steps, its retries, and what happened to it.
- The grain hands the work over and returns. A grain that waited for the job inside the call could not take the job's own call back, since by default it answers one call at a time.
- The enqueue carries an idempotency key. The grain's state and the job are saved separately, so a call that is repeated must find the job the first call made. The grain answers with the job's ref, and the ref is how the rest of the application reaches the job: it is one word, unique across namespaces, so the payment provider's confirmation needs nothing else to find it.
- The grain method a job calls must be safe to receive twice. The call is repeated if the job stopped after the grain was told and before the step was recorded, so the wallet adds a top-up once, by its id.
- A step that finished is not run again. The receipt fails on the first attempt and the job runs a second time: the payment is requested once, the job waits once, and the wallet is told once.
- What the job did is rows in the database: the job, each step with its outcome and each attempt with its error, read with a plain query. The wallet's balance is in memory and in the storage the grain writes to.
- Where your own record lives in the database Acta uses, the enqueue can join your transaction, and the record and its job commit together: [Save a record and start its job in one transaction](/learn/029-enqueue-in-transaction.md). A grain's storage is its own, so here the key does that work.
- A grain can do this alone, and where that is enough it should: it saves how far the top-up got and takes the provider's confirmation as another call. To retry by itself, or to notice a confirmation that never came, it also needs a reminder and its own count of attempts. A job brings those: recorded steps, a wait, a retry policy, and a history an operator can read.
- This program runs the silo and the worker in one application, with localhost clustering and the grain's state in memory storage. It shows nothing about a worker in another process or a cluster of several silos.
- `"request-payment"`, `"add-to-balance"` and `"send-receipt"` are the keys to each step's stored result, and `"payment-confirmed"` names the signal the job waits on. Rename `"request-payment"` and the provider is asked again for a payment it already took.
- A job that waits for a signal or a timer can stay stored for days. The name in `[Job("top-up-wallet")]` and the namespace in `Run<WalletsJobs>("wallets")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `TopUpWallet` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `top-up-wallet-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Check yourself

1. The wallet's `TopUpAsync` is called a second time with the same top-up while its job still exists. What does Acta do?
   - a) It enqueues a second job
   - b) It answers with the first job, since both calls carry the same idempotency key
   - c) It refuses the call with an error
   - Answer: b. The grain keeps no record of its own that it already asked, so the key is what makes the repeated call safe.
2. Why does `TopUpAsync` return as soon as the job is enqueued and not wait for the payment?
   - a) The grain cannot read a job's result
   - b) A grain that waited for the job could not take the job's call back
   - c) Jobs have no results
   - Answer: b. By default a grain answers one call at a time, so `AddToBalanceAsync` would wait behind the `TopUpAsync` that is waiting for it.

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to change a job's input without breaking the jobs already stored](/learn/502-job-versions.md)

## More

- [The whole program](/learn/707-orleans-integration.program.md): every file as compiled, 193 lines.
