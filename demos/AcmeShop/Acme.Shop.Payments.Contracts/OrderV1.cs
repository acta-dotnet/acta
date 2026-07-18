namespace Acme.Shop.Payments.Contracts;

// Durable message enqueued by the API, read by Payments.Worker. Carries order id, user id, amount, and
// line items, never card numbers. Line is nested because it belongs to the order.
public sealed record OrderV1(string OrderId, string UserId, decimal Amount, IReadOnlyList<OrderV1.Line> Lines)
{
    public sealed record Line(string Sku, int Quantity);
}
