namespace Acta.Runtime;

/// <summary>Semantic result for admin control verbs.</summary>
internal sealed record AdminControlOutcome(AdminControlAction Action, int? Version);
