namespace Acta;

/// <summary>
/// The definition id of every descriptor, by job name, with the names the registration added and the names whose stored
/// row it changed or made Active again.
/// </summary>
internal readonly record struct RegisterDefinitionsResponse(
    IReadOnlyDictionary<string, int> DefinitionIds,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Changed
);
