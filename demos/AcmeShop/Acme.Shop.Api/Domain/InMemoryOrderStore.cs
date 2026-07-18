using System.Collections.Concurrent;

namespace Acme.Shop.Api.Domain;

// In-memory stand-in for Acme Shop's order database. Single-process, non-durable, demo-only.
public sealed class InMemoryOrderStore : IOrderStore
{
    private readonly ConcurrentDictionary<string, OrderRecord> _orders = new();
    private readonly ConcurrentQueue<OrderEvent> _events = new();

    public bool Save(OrderRecord order) => _orders.TryAdd(Key(order.UserId, order.OrderId), order);

    public void Append(OrderEvent orderEvent) => _events.Enqueue(orderEvent);

    private static string Key(string userId, string orderId) => $"{userId}/{orderId}";
}
