# The whole program: change a job's input without breaking the jobs already stored

The how-to is [/learn/502-job-versions.md](/learn/502-job-versions.md). This file holds every source file of `learn/500-payloads/502-job-versions` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### job-versions.cs

```csharp
// How to change a job's input without breaking the jobs already stored
// A job enqueued before a deploy can run after it, so the new code has to read the input the old code stored. Keep
// the job's name for a change old rows survive, and give the job a new versioned name for a change they cannot.
using Acta;
using Acta.Learn.JobVersions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddFilter("Acta", LogLevel.Error);
builder.Services.AddSingleton<Invoices>();

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<JobVersionsJobs>("job-versions");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// A row as the first version of the code stored it, before SendInvoice had a Note.
var stored = await jobs.EnqueueAsync(
    new EnqueueJobRequest("job-versions", "send-invoice", JobPayload.Json(new SendInvoiceAsFirstStored("INV-1"))),
    CancellationToken.None
);
var current = await jobs.EnqueueAsync(new SendInvoice("INV-2", "thanks"));

// learn: 4. Enqueue new work under the new name
// The callers move to the new input. The rows already stored under the old name keep their handler.
var next = await jobs.EnqueueAsync(new SendInvoiceV2("INV-3", "USD"));

// learn: end

(await jobs.WaitAsync(stored)).ThrowIfFailed();
(await current.WaitAsync()).ThrowIfFailed();
(await next.WaitAsync()).ThrowIfFailed();

await host.StopAsync();

namespace Acta.Learn.JobVersions
{
    /// <summary>The input as the first version of the code stored it: this program uses it only to write such a row.</summary>
    public sealed record SendInvoiceAsFirstStored(string InvoiceId);

    // learn: 1. Add a field the stored rows can do without
    // Note is optional and has a default, so a row stored before it existed still reads: the job keeps its name.
    public sealed record SendInvoice(string InvoiceId, string? Note = null);

    // learn: end

    // learn: 2. Give a change the stored rows cannot supply a new input
    // Every invoice now needs a currency, and the rows already stored have none. That is a new input, for a new name.
    public sealed record SendInvoiceV2(string InvoiceId, string Currency);

    // learn: end

    public sealed class SendInvoiceJobs(Invoices invoices)
    {
        // learn: 3. Put the new version right under the old one
        // Both call the same service, so the difference between them is one line each. The old handler stays
        // registered until none of its jobs can still run, wait or be restarted; from then on there is one again.
        [Job("send-invoice")]
        public Task V1(SendInvoice input, CancellationToken ct) => invoices.SendAsync(input.InvoiceId, "EUR", input.Note, ct);

        [Job("send-invoice-v2")]
        public Task V2(SendInvoiceV2 input, CancellationToken ct) => invoices.SendAsync(input.InvoiceId, input.Currency, null, ct);

        // learn: end
    }

    /// <summary>The service both versions call.</summary>
    public sealed class Invoices
    {
        public Task SendAsync(string invoiceId, string currency, string? note, CancellationToken ct)
        {
            Console.WriteLine($"sent {invoiceId} in {currency}{(note is null ? "" : $" (note: {note})")}");
            return Task.CompletedTask;
        }
    }
}
```

