namespace Acta;

/// <summary>
/// What a definition control verb did: Applied, VersionConflict when the expected version was stale, or NotFound
/// when no definition carries the name.
/// </summary>
public interface IDefinitionControlResponse : IControlResponse;
