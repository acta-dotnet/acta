namespace Acme.Shop.Api;

public sealed record OrderAccepted(string JobRef, string Action, string StatusUrl);
