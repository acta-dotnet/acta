using Acta;
using Acta.Concepts.CrossNamespaceChild;
using Acta.Payloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);

    // A child can target ANY registered namespace; the completion latch still releases the parent
    // across the boundary.
    j.Run<CrossNamespaceChildJobs>("storefront");
    j.Run<CrossNamespaceChildJobs>("billing");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var order = await jobs.EnqueueAsync(
    new JobEnqueueRequest("storefront", "place-order", JobPayload.Json(new PlaceOrder("order-7", 99.90m))),
    CancellationToken.None
);

while (true)
{
    var status = await jobs.GetStatusAsync(order);
    if (status is { } s && s.IsTerminal)
    {
        break;
    }
    await Task.Delay(100);
}

Console.WriteLine($"order finished: {await jobs.GetResultAsync<OrderConfirmation>(order)}");

await host.StopAsync();

namespace Acta.Concepts.CrossNamespaceChild
{
    public sealed record PlaceOrder(string OrderId, decimal Amount);

    public sealed record ChargePayment(string OrderId, decimal Amount);

    public sealed record PaymentReceipt(string TransactionId);

    public sealed record OrderConfirmation(string OrderId, string TransactionId);

    public sealed class OrderJobs
    {
        // The raw StartChildAsync overload names an explicit (namespace, job) route; the completion
        // latch lands on the parent regardless of where the child ran.
        [Job("place-order")]
        public async Task<OrderConfirmation> Handle(PlaceOrder order, JobContext ctx, CancellationToken ct)
        {
            var charge = await ctx.StartChildAsync(
                "charge",
                "billing",
                "charge-payment",
                JobPayload.Json(new ChargePayment(order.OrderId, order.Amount)),
                ct: ct
            );

            var outcome = await ctx.WaitChildAsync(charge.JobId, ct);
            if (!outcome.Succeeded)
            {
                await ctx.FailAsync($"billing declined ({outcome.Status}); see the child's job events for the reason", ct);
            }

            var receipt = await ctx.GetChildResultAsync<PaymentReceipt>(charge.JobId, ct);
            return new OrderConfirmation(order.OrderId, receipt!.TransactionId);
        }

        [Job("charge-payment")]
        public async Task<PaymentReceipt> Charge(ChargePayment charge, CancellationToken ct)
        {
            await Task.Delay(200, ct);
            Console.WriteLine($"[billing] charged {charge.Amount:0.00} EUR for {charge.OrderId}");
            return new PaymentReceipt($"tx-{charge.OrderId}");
        }
    }
}
