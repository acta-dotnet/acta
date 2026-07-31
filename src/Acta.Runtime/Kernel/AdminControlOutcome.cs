namespace Acta.Runtime.Kernel;

/// <summary>Semantic result for admin control verbs: action plus the resulting version.</summary>
internal sealed record AdminControlOutcome(AdminControlAction Action, int? Version);
