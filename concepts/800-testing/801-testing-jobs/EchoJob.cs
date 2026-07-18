namespace Acta.Concepts.Testing;

public sealed record Echo(string Message);

public sealed record EchoResult(string Message);

// "boom" always throws: retry path is Rearmed, Rearmed, then Failed once MaxAttempts spends. 0s
// backoff keeps each retry due now, so one attempt == one RunOnceAsync tick.
public static class EchoJob
{
    [Job("echo", MaxAttempts = 3, Backoff = "0s")]
    public static EchoResult Handle(Echo input) =>
        input.Message == "boom" ? throw new InvalidOperationException("echo cannot say boom") : new(input.Message);
}
