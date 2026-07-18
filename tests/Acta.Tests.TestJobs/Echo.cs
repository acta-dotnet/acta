using Acta;

namespace TestJobs;

public sealed record Echo(string Message);

public sealed record EchoResult(string Message);

public static class EchoHandler
{
    [Job("echo")]
    public static EchoResult Run(Echo input) => new(input.Message);
}
