namespace Acme.Shop.Api.Domain;

// An append-only entry in Acme Shop's own order history, distinct from Acta's job events.
public sealed record OrderEvent(string OrderId, string Kind, DateTimeOffset AtUtc);
