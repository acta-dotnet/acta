namespace Acta;

/// <summary>The definition id of every descriptor, by job name.</summary>
internal readonly record struct RegisterDefinitionsResponse(IReadOnlyDictionary<string, int> DefinitionIds);
