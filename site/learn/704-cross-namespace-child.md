# How to start a child job in another namespace

A parent starts a child in another worker's namespace and waits across the boundary.

- Section: Split work across child jobs, step 7
- Program: `concepts/700-topology-and-deployment/704-cross-namespace-child` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/704-cross-namespace-child
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Run a worker for each namespace

A child can target ANY registered namespace; the completion latch still releases the parent across the boundary.

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
```

## 2. Name the child's namespace and job

The raw EnqueueChildAsync overload names an explicit (namespace, job) route; the completion latch lands on the parent regardless of where the child ran.

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
public sealed record PlaceOrder(string OrderId, decimal Amount);

public sealed record ChargePayment(string OrderId, decimal Amount);

public sealed record PaymentReceipt(string TransactionId);

public sealed record OrderConfirmation(string OrderId, string TransactionId);

public sealed class OrderJobs
{
    [Job("place-order")]
    public async Task<OrderConfirmation> Handle(PlaceOrder order, JobContext context, CancellationToken ct)
    {
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

        var receipt = await context.GetChildResultAsync<PaymentReceipt>(charge.JobId, ct);
        return new OrderConfirmation(order.OrderId, receipt.TransactionId);
    }

    [Job("charge-payment")]
    public async Task<PaymentReceipt> Charge(ChargePayment charge, CancellationToken ct)
    {
        await Task.Delay(200, ct);
        Console.WriteLine($"[billing] charged {charge.Amount:0.00} EUR for {charge.OrderId}");
        return new PaymentReceipt($"tx-{charge.OrderId}");
    }
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
public sealed record PlaceOrder(string OrderId, decimal Amount);

public sealed record ChargePayment(string OrderId, decimal Amount);

public sealed record PaymentReceipt(string TransactionId);

public sealed record OrderConfirmation(string OrderId, string TransactionId);

public sealed class OrderJobs
{
    [Job("place-order")]
    public async Task<OrderConfirmation> Handle(PlaceOrder order, JobContext context, CancellationToken ct)
    {
        // The raw EnqueueChildAsync overload names an explicit (namespace, job) route; the completion
        // latch lands on the parent regardless of where the child ran.
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

        var receipt = await context.GetChildResultAsync<PaymentReceipt>(charge.JobId, ct);
        return new OrderConfirmation(order.OrderId, receipt.TransactionId);
    }

    [Job("charge-payment")]
    public async Task<PaymentReceipt> Charge(ChargePayment charge, CancellationToken ct)
    {
        await Task.Delay(200, ct);
        Console.WriteLine($"[billing] charged {charge.Amount:0.00} EUR for {charge.OrderId}");
        return new PaymentReceipt($"tx-{charge.OrderId}");
    }
}
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/700-topology-and-deployment/704-cross-namespace-child
```

Its output must contain:

```text
EUR for order-7
order finished: OrderConfirmation { OrderId = order-7, TransactionId = tx-order-7 }
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to enqueue many independent jobs from a handler](/learn/601-fan-out.md)

## More

- [The whole program](/learn/704-cross-namespace-child.program.md): every file as compiled, 93 lines.
