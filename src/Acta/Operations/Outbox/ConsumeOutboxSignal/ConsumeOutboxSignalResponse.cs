namespace Acta;

/// <summary>Whether the applied command row was deleted; false when a newer command superseded it.</summary>
internal readonly record struct ConsumeOutboxSignalResponse(bool Consumed);
