namespace Acta.Runtime;

/// <summary>Semantic result for admin control verbs.</summary>
[DbProjection]
internal sealed record AdminControlOutcome(ControlAction Action, int? Version);
