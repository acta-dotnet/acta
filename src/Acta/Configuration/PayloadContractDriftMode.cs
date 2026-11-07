namespace Acta;

/// <summary>
/// What a deploy does when it would change a definition's contract columns
/// (input or result type, payload format) relative to the stored row.
/// </summary>
public enum PayloadContractDriftMode : byte
{
    /// <summary>Apply the change and log a warning. The default.</summary>
    Warn = 1,

    /// <summary>Fail the deploy before any catalog write.</summary>
    Fail = 2,
}
