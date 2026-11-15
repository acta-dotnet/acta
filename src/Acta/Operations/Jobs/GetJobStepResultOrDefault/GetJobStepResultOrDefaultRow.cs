namespace Acta;

/// <summary>A succeeded step's stored result: its format and bytes.</summary>
[DbProjection]
internal sealed record GetJobStepResultOrDefaultRow(byte ResultFormatId, byte[] Result);
