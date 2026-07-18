namespace Acme.Shop.Shipping.Contracts;

// Durable message enqueued by Payments.Worker, read by Shipping.Worker. No payment data. Line is
// nested; the payments worker maps order lines onto it at the handoff.
public sealed record ShipOrderV1(string OrderId, string UserId, IReadOnlyList<ShipOrderV1.Line> Lines)
{
    public sealed record Line(string Sku, int Quantity);
}
