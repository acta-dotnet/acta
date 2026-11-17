// File: LabelPrinter.cs
namespace Shipping;

public class LabelPrinter
{
    public bool Online { get; set; } = true;

    // Virtual so a test can stand in for it; the real printer client goes here.
    public virtual Task PrintAsync(string trackingNumber, CancellationToken ct) =>
        Online ? Task.CompletedTask : throw new InvalidOperationException($"Printer offline; label {trackingNumber} not printed.");
}
