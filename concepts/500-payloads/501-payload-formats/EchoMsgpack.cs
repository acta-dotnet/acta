using MessagePack;

namespace Acta.Concepts.PayloadFormats;

/// <summary>
/// Guid in a thin record; TIn must stay distinct per handler (manifest enforces unique TIn).
/// </summary>
[MessagePackObject]
public sealed record EchoMsgpack([property: Key(0)] Guid Value);

public sealed class EchoMsgpackHandler
{
    public const int TargetCount = 250;
    private static long _count;

    [Job("echo-msgpack", Format = PayloadFormats.Msgpack)]
    public Task<EchoMsgpack> Handle(EchoMsgpack input, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _count);
        if (n == TargetCount)
        {
            Console.WriteLine($"  msgpack     echo-msgpack   [{n}/{TargetCount}]");
        }
        return Task.FromResult(input);
    }
}
