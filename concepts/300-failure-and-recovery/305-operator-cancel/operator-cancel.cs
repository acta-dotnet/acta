using Acta;
using Acta.Concepts.OperatorCancel;
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

var outcome = await jobs.EnqueueAsync(new BigExport("export-1"));
await Task.Delay(500); // let it start running
Console.WriteLine($"status while running: {await jobs.GetStatusAsync(outcome)}");

// Operator cancel from outside the handler: any process holding an IJobs trips a running job's CancellationToken.
var result = await jobs.CancelAsync(outcome, "operator aborted the export");
Console.WriteLine($"cancel result: {result.Action}");

await Task.Delay(500);
var snapshot = await jobs.GetAsync(outcome);
Console.WriteLine($"final status: {snapshot!.Status}");

await host.StopAsync();

namespace Acta.Concepts.OperatorCancel
{
    public sealed record BigExport(string Id);

    public sealed class BigExportJob
    {
        [Job("big-export")]
        public async Task Handle(BigExport input, CancellationToken ct)
        {
            Console.WriteLine($"[{input.Id}] export started");
            await Task.Delay(TimeSpan.FromSeconds(30), ct); // operator cancels mid-run, so ct trips here
            Console.WriteLine($"[{input.Id}] export finished"); // never reached
        }
    }
}
