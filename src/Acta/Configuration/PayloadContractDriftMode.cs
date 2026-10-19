namespace Acta;

/// <summary>
/// Worker behavior when an eligible registration would change a definition's contract columns
/// (input or output type, payload format) relative to the stored row.
/// </summary>
public enum PayloadContractDriftMode : byte
{
    /// <summary>Apply the change and log a warning. The default.</summary>
    Warn = 1,

    /// <summary>Fail worker startup before any catalog write.</summary>
    Fail = 2,
}
