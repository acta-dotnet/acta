namespace Acta.Runtime;

/// <summary>One name/id row the registration routine returns per registered definition.</summary>
[DbProjection]
internal readonly record struct RegisterJobDefinitionsRow(string Name, int Id);
