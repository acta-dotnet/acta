namespace Acta;

/// <summary>
/// Thrown when a payload exceeds <see cref="JobsOptions.MaxInlinePayloadBytes"/>: a caller-controlled inline write
/// (enqueue input, a job variable or progress payload, a raised signal value), where the write never reached
/// storage; one cap, one exception. Also raised by an oversized HTTP request body, and by a typed read of a result
/// whose body was dropped for exceeding the cap. A result never throws it on the write path, since its body already
/// ran: a handler's is dropped and recorded as <c>result-oversized</c>, and a step's fails the job for good. Names the
/// <paramref name="entryPoint"/> whose payload of <paramref name="actualBytes"/> exceeded <paramref name="maxBytes"/>.
/// </summary>
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
