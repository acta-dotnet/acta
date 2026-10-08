namespace Acta.Runtime;

/// <summary>How many open failure alerts the success closed.</summary>
internal readonly record struct ResolveAlertsResponse(int Resolved);
