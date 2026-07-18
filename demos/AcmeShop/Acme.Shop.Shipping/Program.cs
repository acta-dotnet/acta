using Acme.Shop.Shipping;
using Acta;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);

    // ShippingJobs is the generated manifest (last segment of this project's namespace + "Jobs").
    j.Run<ShippingJobs>("shipping");
});

using var host = builder.Build();

Console.WriteLine("Shipping worker: owns the shipping namespace. Drains the backlog on start.");

await host.RunAsync();
