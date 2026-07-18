namespace Acme.Shop.Api.Domain;

// Application-owned business record in Acme Shop's own store (here in-memory). Acta never sees it.
public sealed record OrderRecord(string OrderId, string UserId, decimal Amount, OrderRecord.OrderStatus Status, DateTimeOffset PlacedAtUtc)
{
    public enum OrderStatus
    {
        Pending,
        Paid,
        Shipped,
        Cancelled,
    }
}
