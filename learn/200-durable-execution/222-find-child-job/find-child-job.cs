// How to find the child job that handles one item
// A child's name on the reads is its deduplication key, `{map}/{key}` for a child that a map started. List the
// parent's children and pick the one that carries the item's key.
using Acta;
using Acta.Learn.FindChildJob;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<FindChildJobJobs>("find-child-job");
});

// One invoice fails on purpose; quiet the framework's warning for it.
builder.Logging.AddFilter("Acta", LogLevel.Error);

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();
var operations = host.Services.GetRequiredService<IActaOperations>();

var parent = await jobs.EnqueueAsync(new SendInvoices([5, 6, 7]));
await parent.WaitAsync();

// learn: 2. List the parent's children
// `Parent` restricts the job list to the children of one job. Each child shows the name it was started under.
var children = await operations.Ledger.ListJobsAsync(new ListJobsQuery(Parent: parent));
foreach (var child in children.Items.OrderBy(child => child.DeduplicationKey, StringComparer.Ordinal))
{
    Console.WriteLine($"{child.DeduplicationKey}: {child.Status.Code}");
}

// learn: end

// learn: 3. Pick the child that carries the item's key
// The key is the one the map was given for that item, so a page that shows invoice 7 gets from the invoice to its
// job, and from there to what the job did.
var invoice7 = children.Items.Single(child => child.DeduplicationKey == "invoice/7");
var why = (await jobs.ExplainAsync(invoice7.JobRef))!;
Console.WriteLine($"invoice 7: {why.Headline} {why.Reason}");

// learn: end

await host.StopAsync();

namespace Acta.Learn.FindChildJob
{
    public sealed record SendInvoices(int[] InvoiceIds);

    public sealed record SendInvoice(int InvoiceId);

    public sealed class InvoiceJobs
    {
        [Job("send-invoices")]
        public async Task Handle(SendInvoices batch, JobContext ctx, CancellationToken ct)
        {
            // learn: 1. Start each child under the item's own key
            // `itemKey` names each child after its item. The name is stored as the child's deduplication key, and it
            // is how a replay of the parent and a reader from outside find the same child.
            var sent = await ctx.MapAsync("invoice", batch.InvoiceIds, itemKey: id => id, child: id => new SendInvoice(id), ct);
            // learn: end
            Console.WriteLine($"{sent.Items.Count(item => item.Outcome.Succeeded)} of {batch.InvoiceIds.Length} invoices sent");
        }

        [Job("send-invoice")]
        public async Task Send(SendInvoice invoice, JobContext ctx, CancellationToken ct)
        {
            if (invoice.InvoiceId == 7)
            {
                await ctx.FailAsync("the customer's address bounced", ct);
            }
            Console.WriteLine($"invoice {invoice.InvoiceId} sent");
        }
    }
}
