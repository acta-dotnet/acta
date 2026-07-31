using Acme.Shop.Payments.Contracts;
using Acme.Shop.Shipping.Contracts;
using Acta;

namespace Acme.Shop.Payments;

// Built fresh per attempt with DI-injected ctor. IJobs hands shipping off into the other namespace.
public sealed class ProcessPaymentJob(IJobs jobs)
{
    [Job("process-payment")]
    public async Task Handle(OrderV1 order, JobContext ctx, CancellationToken ct)
    {
        // Fraud review runs before any charge; a rejected order is cancelled with nothing to refund.
        if (order.Amount >= 500m)
        {
            var decision = await ctx.WaitSignalAsync<FraudDecisionV1>("fraud-review", ct);
            if (decision is not { Approved: true })
            {
                await JobContext.CancelAsync($"fraud review rejected by {decision?.Reviewer}", ct);
                return;
            }

            Console.WriteLine($"[{order.OrderId}] fraud review approved by {decision.Reviewer}");
        }

        await ctx.RunStepAsync(
            "reserve-stock",
            async innerCt =>
            {
                await Task.Delay(150, innerCt);
                Console.WriteLine($"[{order.OrderId}] stock reserved");
            },
            ct: ct
        );

        var charge = await ctx.RunStepAsync(
            "charge-card",
            async innerCt =>
            {
                await Task.Delay(150, innerCt);
                Console.WriteLine($"[{order.OrderId}] card charged {order.Amount:0.00}");
                return new ChargeResult($"ch_{Guid.CreateVersion7():N}");
            },
            retry => retry.MaxAttempts(5).BackoffInitialDelay(TimeSpan.FromSeconds(2)),
            ct
        );

        // Handoff: map lines onto shipping's line type and enqueue into the shipping namespace.
        // ship:{userId}:{orderId} dedupes the re-enqueue (Acta job creation only).
        var lines = order.Lines.Select(l => new ShipOrderV1.Line(l.Sku, l.Quantity)).ToList();
        var handoff = new JobEnqueueRequest(
            "shipping",
            "ship-order",
            JobPayload.Json(new ShipOrderV1(order.OrderId, order.UserId, lines)),
            DeduplicationKey: $"ship:{order.UserId}:{order.OrderId}"
        );

        await jobs.EnqueueAsync(handoff, ct);
        Console.WriteLine($"[{order.OrderId}] payment complete (charge {charge.ChargeId}), handed off to shipping");
    }

    // The charge-card step's recorded result.
    public sealed record ChargeResult(string ChargeId);
}
