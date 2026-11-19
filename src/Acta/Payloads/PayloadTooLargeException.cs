namespace Acta;

/// <summary>
/// Thrown when a payload exceeds <see cref="JobsOptions.MaxInlinePayloadBytes"/>: a caller-controlled inline write
/// (enqueue input, a job variable or progress payload, a raised signal value), where the write never reached
/// storage, and an oversized HTTP request body; one cap, one exception. A result never throws it, since its body
/// already ran: a handler's or a step's result past the cap is stored with a warning, up to the result ceiling. Names the
/// <paramref name="entryPoint"/> whose payload of <paramref name="actualBytes"/> exceeded <paramref name="maxBytes"/>.
/// </summary>
public sealed class PayloadTooLargeException(string entryPoint, int actualBytes, int maxBytes)
    : ActaException($"{entryPoint} payload is {actualBytes} bytes, exceeding the {maxBytes}-byte MaxInlinePayloadBytes limit.")
{
    /// <summary>The write that was rejected (e.g. <c>"enqueue input"</c>, <c>"variable 'foo'"</c>).</summary>
    public string EntryPoint { get; } = entryPoint;

    /// <summary>The size of the rejected payload in bytes.</summary>
    public int ActualBytes { get; } = actualBytes;

    /// <summary>The configured <see cref="JobsOptions.MaxInlinePayloadBytes"/> cap.</summary>
    public int MaxBytes { get; } = maxBytes;
}
