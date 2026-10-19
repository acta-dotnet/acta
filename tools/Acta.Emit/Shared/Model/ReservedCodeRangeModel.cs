namespace Acta.Emit.Shared.Model;

internal sealed record ReservedCodeRangeModel(byte Start, byte End, string Reason, bool PermanentlyUnavailable);
