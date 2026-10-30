// File: Jobs.cs
using Acta;

namespace Shipping;

public sealed record ShipOrder(int OrderId);

public sealed record ShipmentLabel(int OrderId, string Carrier, string TrackingNumber);

public sealed record PrintLabel(string TrackingNumber);

public sealed class OrderHandlers(LabelPrinter printer)
{
    // A result-bearing job: the generated call ShippingJobs.ShipOrder(...) carries the result type, so the
    // handle it answers is a JobHandle<ShipmentLabel>.
    [Job("ship-order")]
    public Task<ShipmentLabel> Ship(ShipOrder input, CancellationToken ct) =>
        Task.FromResult(new ShipmentLabel(input.OrderId, "acme-post", $"AP{input.OrderId:D8}"));

    // One attempt: a printer that is off is not retried for a day; the job fails for good and the
    // explanation says why. The default is 15 attempts with backoff, which suits a flaky dependency.
    [Job("print-label", MaxAttempts = 1)]
    public Task Print(PrintLabel input, CancellationToken ct) => printer.PrintAsync(input.TrackingNumber, ct);
}
