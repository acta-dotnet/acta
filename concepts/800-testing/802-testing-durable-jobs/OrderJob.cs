// Durable multi-step job under test: reserve stock, wait for an approval signal, then charge.

namespace Acta.Concepts.TestingDurable;

public sealed record PlaceOrder(string OrderId, decimal Amount);

public sealed record ApprovalDecision(bool Approved, string By);

public sealed record OrderResult(string OrderId, bool Charged);

public static class OrderJob
{
    // Counts reserve step body executions so a test can prove run-once across suspend/replay.
    public static int ReserveCount;

    [Job("process-order")]
    public static async Task<OrderResult> Handle(PlaceOrder order, JobContext context, CancellationToken ct)
    {
        await context.RunStepAsync(
            "reserve-stock",
            _ =>
            {
                Interlocked.Increment(ref ReserveCount);
                return Task.CompletedTask;
            },
            ct: ct
        );

        // Suspend until the signal arrives; on resume the job replays from the top but the recorded
        // reserve step is NOT re-run.
        var decision = await context.WaitSignalAsync<ApprovalDecision>("approval", ct);
        if (!decision!.Approved)
        {
            return new OrderResult(order.OrderId, Charged: false);
        }

        await context.RunStepAsync("charge", _ => Task.CompletedTask, ct: ct);
        return new OrderResult(order.OrderId, Charged: true);
    }
}
