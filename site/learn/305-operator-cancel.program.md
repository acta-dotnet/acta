# The whole program: cancel a running job from outside

The how-to is [/learn/305-operator-cancel.md](/learn/305-operator-cancel.md). This file holds every source file of `learn/300-failure-and-recovery/305-operator-cancel` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### operator-cancel.cs

```csharp
// How to cancel a running job from outside
// `CancelAsync` on a job's handle cancels it from outside its handler. Any process that holds an `IJobs` can do it,
// and a running handler's `CancellationToken` trips.
using Acta;
using Acta.Learn.OperatorCancel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<OperatorCancelJobs>("operator-cancel");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var handle = await jobs.EnqueueAsync(new BigExport("export-1"));
await Task.Delay(500); // let it start running
Console.WriteLine($"status while running: {(await handle.GetStatusAsync())?.Status}");

// learn: Cancel the job from outside
// Operator cancel from outside the handler: any process holding an IJobs trips a running job's CancellationToken.
var result = await handle.CancelAsync("operator aborted the export");
Console.WriteLine($"cancel result: {result.Action}");

// learn: end

await handle.WaitAsync();
var snapshot = await jobs.GetAsync(handle);
Console.WriteLine($"final status: {snapshot!.Status}");

await host.StopAsync();

namespace Acta.Learn.OperatorCancel
{
    public sealed record BigExport(string Id);

    public sealed class BigExportJob
    {
        [Job("big-export")]
        public async Task Handle(BigExport input, CancellationToken ct)
        {
            Console.WriteLine($"[{input.Id}] export started");
            // learn: Pass the token to the work, so the cancel can stop it
            // The handler hands its CancellationToken to everything it awaits; the cancel from outside ends the wait here.
            await Task.Delay(TimeSpan.FromSeconds(30), ct); // operator cancels mid-run, so ct trips here
            // learn: end
            Console.WriteLine($"[{input.Id}] export finished"); // never reached
        }
    }
}
```

