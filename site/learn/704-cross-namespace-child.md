# How to start a child job in another namespace

A parent starts a child in another worker's namespace and waits across the boundary.

- Section: Split work across child jobs, step 7
- Program: `learn/700-topology-and-deployment/704-cross-namespace-child` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/704-cross-namespace-child
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Run a worker for each namespace

A child can target any registered namespace: the parent's wait is released when the child finishes, whichever namespace ran it.

The lines that matter:

```csharp
j.Run<CrossNamespaceChildJobs>("storefront");
j.Run<CrossNamespaceChildJobs>("billing");
```

In `cross-namespace-child.cs` (excerpt):

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);

    j.Run<CrossNamespaceChildJobs>("storefront");
    j.Run<CrossNamespaceChildJobs>("billing");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();
```

## 2. Name the child's namespace and job

The raw EnqueueChildAsync overload names an explicit (namespace, job) route. The first argument is the child's name: on a re-entry the same name finds the child already enqueued, so it is sent once.

The lines that matter:

```csharp
var charge = await context.EnqueueChildAsync(
    "charge",
    "billing",
    "charge-payment",
    JobPayload.Json(new ChargePayment(order.OrderId, order.Amount)),
    ct: ct
);
```

In `cross-namespace-child.cs` (excerpt):

```csharp
var charge = await context.EnqueueChildAsync(
    "charge",
    "billing",
    "charge-payment",
    JobPayload.Json(new ChargePayment(order.OrderId, order.Amount)),
    ct: ct
);

var outcome = await context.WaitChildAsync(charge, ct);
if (!outcome.Succeeded)
{
    await context.FailAsync($"billing declined ({outcome.Status}); see the child's job events for the reason", ct);
}
```

## 3. Wait for the child and read its result

The lines that matter:

```csharp
var outcome = await context.WaitChildAsync(charge, ct);
if (!outcome.Succeeded)
{
    await context.FailAsync($"billing declined ({outcome.Status}); see the child's job events for the reason", ct);
}

var receipt = await context.GetChildResultAsync<PaymentReceipt>(charge.JobId, ct);
```

In `cross-namespace-child.cs` (excerpt):

```csharp
var outcome = await context.WaitChildAsync(charge, ct);
if (!outcome.Succeeded)
{
    await context.FailAsync($"billing declined ({outcome.Status}); see the child's job events for the reason", ct);
}

var receipt = await context.GetChildResultAsync<PaymentReceipt>(charge.JobId, ct);
return new OrderConfirmation(order.OrderId, receipt.TransactionId);
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/700-topology-and-deployment/704-cross-namespace-child
```

Its output must contain:

```text
EUR for order-7
order finished: OrderConfirmation { OrderId = order-7, TransactionId = tx-order-7 }
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- `"charge"` is how `place-order` finds the payment it already started in the other namespace. A renamed child is new work: the payment is charged again.
- A parent and its children are each stored under their own job name. The names in `[Job("place-order")]` and `[Job("charge-payment")]` and the namespaces `"storefront"` and `"billing"` are stored with every job. Treat them as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `PlaceOrder` is stored with each job as its input, and `OrderConfirmation` as its result. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `place-order-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to enqueue many independent jobs from a handler](/learn/601-fan-out.md)

## More

- [The whole program](/learn/704-cross-namespace-child.program.md): every file as compiled, 93 lines.
