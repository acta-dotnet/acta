namespace Acta.Runtime;

/// <summary>Flat input row (input_format_id, input); the input buffer is NULL for a no-input job.</summary>
[DbProjection]
internal sealed record GetJobInputRow(byte FormatId, byte[]? Input);
