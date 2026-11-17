namespace Acta.Concepts.PayloadFormats;

/// <summary>
/// Guid in a thin record, encoded with <c>json-gzip</c>; at this size gzip framing makes it the largest wire format.
/// </summary>
public sealed record EchoGzip(Guid Value);

public sealed class EchoGzipHandler
{
    public const int TargetCount = 250;
    private static long _count;

    // learn: 3. Name the format on the job
    [Job("echo-gzip", Format = PayloadFormats.JsonGzip)]
    // learn: end
    public Task<EchoGzip> Handle(EchoGzip input, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _count);
        if (n == TargetCount)
        {
            Console.WriteLine($"  json-gzip   echo-gzip      [{n}/{TargetCount}]");
        }
        return Task.FromResult(input);
    }
}
