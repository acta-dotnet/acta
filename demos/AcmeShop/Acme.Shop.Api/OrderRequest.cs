using Acme.Shop.Payments.Contracts;

namespace Acme.Shop.Api;

// HTTP request body. Reuses OrderV1.Line so the API maps straight onto the durable message it enqueues.
public sealed record OrderRequest(string OrderId, decimal Amount, IReadOnlyList<OrderV1.Line> Lines);
