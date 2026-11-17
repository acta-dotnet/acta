// How to cancel a running job from outside
// `CancelAsync` on a job's handle cancels it from outside its handler. Any process that holds an `IJobs` can do it,
// and a running handler's `CancellationToken` trips.
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

namespace Acta.Concepts.OperatorCancel
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
