using Acme.Shop.Shipping.Contracts;
using Acta;

namespace Acme.Shop.Shipping;

public sealed class ShipOrderJob
{
    [Job("ship-order")]
    public static async Task Handle(ShipOrderV1 order, JobContext ctx, CancellationToken ct)
    {
        // Run-once step: on replay the recorded result is restored, so notify-customer reads the
        // tracking number from this local without a durable variable.
        var label = await ctx.RunStepAsync(
            "create-label",
            async innerCt =>
            {
                await Task.Delay(120, innerCt);
                var tracking = $"1Z{Guid.CreateVersion7():N}"[..18].ToUpperInvariant();
                Console.WriteLine($"[{order.OrderId}] label created {tracking}");
                return new ShippingLabel(tracking);
            },
            ct: ct
        );

        await ctx.RunStepAsync(
            "dispatch-package",
            async innerCt =>
            {
                await Task.Delay(120, innerCt);
                Console.WriteLine($"[{order.OrderId}] dispatched to carrier");
            },
            ct: ct
        );

        await ctx.RunStepAsync(
            "notify-customer",
            async innerCt =>
            {
                await Task.Delay(100, innerCt);
                Console.WriteLine($"[{order.OrderId}] customer notified, tracking {label.TrackingNumber}");
            },
            ct: ct
        );

        Console.WriteLine($"[{order.OrderId}] shipped");
    }

    // The create-label step's recorded result.
    public sealed record ShippingLabel(string TrackingNumber);
}
