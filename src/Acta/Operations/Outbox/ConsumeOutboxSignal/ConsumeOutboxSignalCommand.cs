namespace Acta;

/// <summary>The applied command row to delete, matched by the stored value the applying tick read.</summary>
internal sealed record ConsumeOutboxSignalCommand(long JobId, string Name, byte[] Value);
