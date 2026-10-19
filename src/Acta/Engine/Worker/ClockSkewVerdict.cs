namespace Acta;

/// <summary>
/// The outcome of a worker-init clock-skew check.
/// </summary>
internal enum ClockSkewVerdict
{
    /// <summary>Measured skew is within the warn threshold.</summary>
    WithinTolerance,

    /// <summary>Measured skew exceeds the warn threshold but not the fail threshold; a warning was logged.</summary>
    Warned,

    /// <summary>Measured skew exceeds the fail threshold, but <c>AllowClockSkew</c> downgraded the failure to a warning.</summary>
    ExceededButAllowed,
}
