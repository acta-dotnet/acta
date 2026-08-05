namespace Acta;

/// <summary>
/// Thrown when a payload exceeds <see cref="JobsOptions.MaxInlinePayloadBytes"/>: a caller-controlled
/// inline write (enqueue input, a job variable or progress payload, a raised signal value, a step
/// result), where the write never reached storage. One cap, one exception.
/// </summary>
/// <remarks>
/// Also raised by an oversized HTTP request body, and by a typed read of a result whose body was
/// dropped for exceeding the cap. Handler results themselves never throw on the write path: the job
/// already ran, so its body is dropped and recorded as <c>job.result-oversized</c> instead of
/// stranding it.
/// </remarks>
/// <remarks>
/// Creates the exception for the named <paramref name="entryPoint"/> whose payload of
/// <paramref name="actualBytes"/> exceeded the <paramref name="maxBytes"/> cap.
/// </remarks>
public sealed class PayloadTooLargeException(string entryPoint, int actualBytes, int maxBytes)
    : Exception($"{entryPoint} payload is {actualBytes} bytes, exceeding the {maxBytes}-byte MaxInlinePayloadBytes limit.")
{
    /// <summary>The write that was rejected (e.g. <c>"enqueue input"</c>, <c>"variable 'foo'"</c>).</summary>
    public string EntryPoint { get; } = entryPoint;

    /// <summary>The size of the rejected payload in bytes.</summary>
    public int ActualBytes { get; } = actualBytes;

    /// <summary>The configured <see cref="JobsOptions.MaxInlinePayloadBytes"/> cap.</summary>
    public int MaxBytes { get; } = maxBytes;
}
