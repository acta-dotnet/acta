namespace Acme.Shop.Payments.Contracts;

// The fraud-review signal payload an operator raises to release or reject a held high-value order.
public sealed record FraudDecisionV1(bool Approved, string Reviewer);
