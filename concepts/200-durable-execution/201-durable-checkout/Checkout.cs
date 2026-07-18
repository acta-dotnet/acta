namespace Acta.Concepts.DurableCheckout;

public sealed record Checkout(string OrderId, decimal Amount);

public sealed record ChargeResult(string ChargeId);

public sealed record FraudDecision(bool Approved, string Reviewer);

public sealed record CheckoutResult(string OrderId, string ChargeId);

public sealed class CheckoutJob
{
    [Job("checkout")]
    public async Task<CheckoutResult> Handle(Checkout order, JobContext ctx, CancellationToken ct)
    {
        // Normal durable steps replay completed outcomes, but an in-flight body can run again after a
        // crash. These simulated external calls therefore carry stable deduplication keys. A real
        // inventory, payment, or mail service must persist and honor the same contract.
        await ctx.RunStepAsync(
            "reserve-inventory",
            async innerCt =>
            {
                await Task.Delay(150, innerCt);
                Console.WriteLine($"[{order.OrderId}] inventory request: deduplication-key=reservation:{order.OrderId}");
            },
            ct: ct
        );

        var charge = await ctx.RunStepAsync(
            "charge-card",
            async innerCt =>
            {
                await Task.Delay(150, innerCt);
                Console.WriteLine($"[{order.OrderId}] payment request: deduplication-key=payment:{order.OrderId}");
                return new ChargeResult($"ch_{order.OrderId}");
            },
            ct: ct
        );

        // Get-or-set demonstrates that the variable row is read, not rewritten, on handler re-entry.
        var chargeId = await ctx.GetOrSetVariableAsync("charge-id", () => charge.ChargeId, ct);

        if (order.Amount >= 500m)
        {
            // No thread or executor waits here. Acta stores the signal checkpoint, releases the lease,
            // and re-enters this handler from the top after another actor raises the signal.
            var decision = await ctx.WaitSignalAsync<FraudDecision>("fraud-review", ct);
            if (decision is not { Approved: true })
            {
                await ctx.CancelAsync($"fraud review rejected by {decision?.Reviewer}", ct);
            }

            // Deliberately ordinary, repeat-safe diagnostic code: it runs after the signal replay and
            // again after the timer replay. External side effects belong in idempotent durable steps.
            Console.WriteLine($"[{order.OrderId}] approval observed by handler; reviewer={decision!.Reviewer}");
        }

        // The named timer stores an intention in SQL. It does not preserve a process-local continuation.
        await ctx.SleepAsync("settlement-delay", TimeSpan.FromSeconds(5), "simulated settlement window", ct);

        await ctx.RunStepAsync(
            "send-receipt",
            async innerCt =>
            {
                var storedChargeId = await ctx.GetRequiredVariableAsync<string>("charge-id", innerCt);
                await Task.Delay(100, innerCt);
                Console.WriteLine($"[{order.OrderId}] receipt request for {storedChargeId}: deduplication-key=receipt:{order.OrderId}");
            },
            ct: ct
        );

        return new CheckoutResult(order.OrderId, chargeId);
    }
}
