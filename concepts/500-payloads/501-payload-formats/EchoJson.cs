namespace Acta.Concepts.PayloadFormats;

/// <summary>A normal JSON baseline: human-readable in <c>jobs_view</c>, with no custom serializer.</summary>
public sealed record EchoJson(Guid Value);

public sealed class EchoJsonHandler
{
    public const int TargetCount = 250;
    private static long _count;

    [Job("echo-json")]
    public Task<EchoJson> Handle(EchoJson input, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _count);
        if (n == TargetCount)
        {
            Console.WriteLine($"  json         echo-json      [{n}/{TargetCount}]");
        }
        return Task.FromResult(input);
    }
}
