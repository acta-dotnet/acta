using Acta;
using Acta.Demos.ApiWorkerSplit;
using Acta.Demos.ApiWorkerSplit.Contracts;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);

    // Only this process owns the handler manifest and a worker loop; more terminals share the claim load.
    j.Run<ApiWorkerSplitJobs>(WelcomeEmailRoute.Namespace);
});

using var host = builder.Build();

Console.WriteLine("Worker process: owns the welcome-emails namespace.");
Console.WriteLine("Start more worker terminals to scale; stop the API and this keeps draining.");

await host.RunAsync();
