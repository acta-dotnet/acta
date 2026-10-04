namespace Acta.Runtime;

/// <summary>
/// One operator command to park on the slot's signal checkpoint: inserted when the slot is free, superseding
/// a pending command older than <paramref name="StaleBeforeUtc"/> (the worker-dead window, so nothing live
/// races the overwrite), rejected otherwise. Version-CAS on the checkpoint keeps overwrite and apply safe.
/// </summary>
internal sealed record ParkOutboxSignalCommand(long JobId, string Name, byte ValueFormatId, byte[] Value, DateTime StaleBeforeUtc);
