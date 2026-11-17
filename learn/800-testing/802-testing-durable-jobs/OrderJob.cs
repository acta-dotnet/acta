// Durable multi-step job under test: reserve stock, wait for an approval signal, then charge.

using System.Collections.Concurrent;

namespace Acta.Learn.TestingDurable;

public sealed record PlaceOrder(string OrderId, decimal Amount);

public sealed record ApprovalDecision(bool Approved, string By);

public sealed record OrderResult(string OrderId, bool Charged);

// The stock system the job reserves against. A test registers its own instance and reads how many times an order's
// reservation ran, so the run-once proof holds per order even while tests share the host.
public sealed class Warehouse
{
    private readonly ConcurrentDictionary<string, int> _reservations = new();

    public void Reserve(string orderId) => _reservations.AddOrUpdate(orderId, 1, (_, count) => count + 1);

    public int ReservationsFor(string orderId) => _reservations.GetValueOrDefault(orderId);
}

public sealed class OrderJob(Warehouse warehouse)
{
    [Job("process-order")]
    public async Task<OrderResult> Handle(PlaceOrder order, JobContext context, CancellationToken ct)
    {
        await context.RunStepAsync(
            "reserve-stock",
            _ =>
            {
                warehouse.Reserve(order.OrderId);
                return Task.CompletedTask;
            },
            ct
        );

        // Suspend until the signal arrives; on resume the job replays from the top but the recorded
        // reserve step is NOT re-run.
        var decision = await context.WaitSignalAsync<ApprovalDecision>("approval", ct);
        if (!decision!.Approved)
        {
            return new OrderResult(order.OrderId, Charged: false);
        }

        await context.RunStepAsync("charge", _ => Task.CompletedTask, ct);
        return new OrderResult(order.OrderId, Charged: true);
    }
}
