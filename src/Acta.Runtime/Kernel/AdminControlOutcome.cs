namespace Acta.Runtime.Kernel;

/// <summary>Semantic result for admin control verbs.</summary>
internal sealed record AdminControlOutcome(AdminControlAction Action, int? Version);
