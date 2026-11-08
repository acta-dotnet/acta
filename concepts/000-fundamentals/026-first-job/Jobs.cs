// File: Jobs.cs
using Acta;

namespace Shipping;

public sealed record ShipOrder(int OrderId);

public sealed record ShipmentLabel(int OrderId, string Carrier, string TrackingNumber);

public sealed record PrintLabel(string TrackingNumber);

public sealed class OrderHandlers(Carrier carrier, LabelPrinter printer)
{
    // A result-bearing job: its handler books the shipment and returns the label. The default policy, 15 attempts
    // with backoff from one minute, suits a flaky carrier: a timeout costs an attempt, not the order.
    [Job("ship-order")]
    public async Task<ShipmentLabel> Ship(ShipOrder input, CancellationToken ct) =>
        new(input.OrderId, "acme-post", await carrier.BookAsync(input.OrderId, ct));

    // One attempt: a printer that is off is not retried for a day; the job fails for good and the
    // explanation says why.
    [Job("print-label", MaxAttempts = 1)]
    public Task Print(PrintLabel input, CancellationToken ct) => printer.PrintAsync(input.TrackingNumber, ct);
}
