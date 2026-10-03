namespace Acta;

/// <summary>
/// What a definition control verb did: Applied, Rejected when the expected version was stale, or NotFound
/// when no definition carries the name.
/// </summary>
public interface IDefinitionControlResponse
{
    /// <summary>The outcome; a verb's own documentation says which actions it can answer.</summary>
    ControlAction Action { get; }
}
