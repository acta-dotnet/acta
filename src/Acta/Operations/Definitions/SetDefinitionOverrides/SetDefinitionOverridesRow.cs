namespace Acta;

/// <summary>The action the override routine took, its single result row.</summary>
[DbProjection(ByName = true)]
internal sealed record SetDefinitionOverridesRow(DefinitionOverrideAction Action);
