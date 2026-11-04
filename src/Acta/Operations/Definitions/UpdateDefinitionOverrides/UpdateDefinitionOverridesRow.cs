namespace Acta;

/// <summary>The action the override routine took and the definition's version after it, its single result row.</summary>
[DbProjection(ByName = true)]
internal sealed record UpdateDefinitionOverridesRow(DefinitionOverrideAction Action, int? Version);
