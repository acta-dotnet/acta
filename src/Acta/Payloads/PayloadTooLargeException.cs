namespace Acta;

/// <summary>
/// Thrown by a caller-controlled inline payload write (enqueue input, a job variable or progress
/// payload, or a raised signal value) when the serialized bytes exceed
/// <see cref="JobsOptions.MaxInlinePayloadBytes"/>. The write never reached storage.
/// </summary>
/// <remarks>
/// Handler results are deliberately exempt: they warn-and-persist rather than throwing, so a large
/// result never strands a job that already ran.
/// </remarks>
public sealed class PayloadTooLargeException : Exception
{
    /// <summary>
    /// Creates the exception for the named <paramref name="entryPoint"/> whose payload of
    /// <paramref name="actualBytes"/> exceeded the <paramref name="maxBytes"/> cap.
    /// </summary>
    public PayloadTooLargeException(string entryPoint, int actualBytes, int maxBytes)
        : base($"{entryPoint} payload is {actualBytes} bytes, exceeding the {maxBytes}-byte MaxInlinePayloadBytes limit.")
    {
        EntryPoint = entryPoint;
        ActualBytes = actualBytes;
        MaxBytes = maxBytes;
    }

    /// <summary>The write that was rejected (e.g. <c>"enqueue input"</c>, <c>"variable 'foo'"</c>).</summary>
    public string EntryPoint { get; }

    /// <summary>The size of the rejected payload in bytes.</summary>
    public int ActualBytes { get; }

    /// <summary>The configured <see cref="JobsOptions.MaxInlinePayloadBytes"/> cap.</summary>
    public int MaxBytes { get; }
}
