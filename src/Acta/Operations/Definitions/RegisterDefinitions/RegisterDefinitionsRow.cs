namespace Acta;

/// <summary>One name/id row the registration returns per registered definition.</summary>
[DbProjection]
internal readonly record struct RegisterDefinitionsRow(string Name, int Id);
