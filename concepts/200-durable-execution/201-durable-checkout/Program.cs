using Acta;
using Acta.Concepts.DurableCheckout;
using Acta.Labs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var runId = Guid.NewGuid().ToString("N")[..16];
var scenario = new CheckoutLabScenario(
    Namespace: $"checkout-lab-{runId}",
    OrderId: $"order-{runId}",
    RejectFraudReview: args.Contains("--reject", StringComparer.OrdinalIgnoreCase)
);

builder.Services.AddSingleton(scenario);
builder.Services.AddSingleton(new ConceptLab(builder.Configuration, args));

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<DurableCheckoutJobs>(scenario.Namespace);
});

builder.Services.AddHostedService<Primer>();

await builder.Build().RunAsync();
