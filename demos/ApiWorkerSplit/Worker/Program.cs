using Acta;
using Acta.Demos.ApiWorkerSplit;
using Acta.Demos.ApiWorkerSplit.Contracts;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);

    // Only this process owns the handler manifest and a worker loop.
    j.Run<ApiWorkerSplitJobs>(WelcomeEmailRoute.Namespace);
});

using var host = builder.Build();

Console.WriteLine("Worker process: owns the welcome-emails namespace.");
Console.WriteLine("Stop the API and this keeps draining. On PostgreSQL or SQL Server, more worker terminals share the work.");

await host.RunAsync();
