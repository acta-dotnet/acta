// How to start a child job in another namespace
// A parent starts a child in another worker's namespace and waits across the boundary.
using Acta;
using Acta.Learn.CrossNamespaceChild;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);

    // learn: 1. Run a worker for each namespace
    // A child can target ANY registered namespace; the completion latch still releases the parent
    // across the boundary.
    j.Run<CrossNamespaceChildJobs>("storefront");
    j.Run<CrossNamespaceChildJobs>("billing");
    // learn: end
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var order = await jobs.EnqueueAsync(
    new EnqueueJobRequest("storefront", "place-order", JobPayload.Json(new PlaceOrder("order-7", 99.90m))),
    CancellationToken.None
);

while (true)
{
    var status = (await jobs.GetStatusAsync(order))?.Status;
    if (status is { } s && s.IsTerminal)
    {
        break;
    }
    await Task.Delay(100);
}

Console.WriteLine($"order finished: {await jobs.GetResultAsync<OrderConfirmation>(order)}");

await host.StopAsync();

namespace Acta.Learn.CrossNamespaceChild
{
    public sealed record PlaceOrder(string OrderId, decimal Amount);

    public sealed record ChargePayment(string OrderId, decimal Amount);

    public sealed record PaymentReceipt(string TransactionId);

    public sealed record OrderConfirmation(string OrderId, string TransactionId);

    public sealed class OrderJobs
    {
        [Job("place-order")]
        public async Task<OrderConfirmation> Handle(PlaceOrder order, JobContext context, CancellationToken ct)
        {
            // learn: 2. Name the child's namespace and job
            // The raw EnqueueChildAsync overload names an explicit (namespace, job) route; the completion
            // latch lands on the parent regardless of where the child ran.
            var charge = await context.EnqueueChildAsync(
                "charge",
                "billing",
                "charge-payment",
                JobPayload.Json(new ChargePayment(order.OrderId, order.Amount)),
                ct: ct
            );
            // learn: end

            // learn: 3. Wait for the child and read its result
            var outcome = await context.WaitChildAsync(charge, ct);
            if (!outcome.Succeeded)
            {
                await context.FailAsync($"billing declined ({outcome.Status}); see the child's job events for the reason", ct);
            }

            var receipt = await context.GetChildResultAsync<PaymentReceipt>(charge.JobId, ct);
            // learn: end
            return new OrderConfirmation(order.OrderId, receipt.TransactionId);
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
