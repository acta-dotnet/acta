using Acme.Shop.Payments;
using Acta;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);

    // PaymentsJobs is the generated manifest (last segment of this project's namespace + "Jobs").
    j.Run<PaymentsJobs>("payments");
});

using var host = builder.Build();

Console.WriteLine("Payments worker: owns the payments namespace. Hands shipping off durably.");

await host.RunAsync();
