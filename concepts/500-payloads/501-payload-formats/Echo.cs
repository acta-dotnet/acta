namespace Acta.Concepts.PayloadFormats;

/// <summary>
/// Echo a Guid via <c>scalar-v1</c>: 16 bytes on the wire in each direction.
/// </summary>
public sealed class EchoScalarHandler
{
    public const int TargetCount = 250;
    private static long _count;

    [Job("echo-scalar", Format = PayloadFormats.ScalarV1)]
    public Task<Guid> Handle(Guid input, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _count);
        if (n == TargetCount)
        {
            Console.WriteLine($"  scalar-v1   echo-scalar    [{n}/{TargetCount}]");
        }
        return Task.FromResult(input);
    }
}
