namespace Acta;

/// <summary>Whether the applied command row was deleted; false when the command read is no longer the one stored.</summary>
internal readonly record struct ConsumeOutboxSignalResponse(bool Consumed);
