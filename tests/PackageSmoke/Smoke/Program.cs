using Acta;
using Smoke;

// SmokeJobs only exists if the source generator ran from the package's analyzer assets.
Console.WriteLine($"generated manifest: {typeof(SmokeJobs).FullName}");

// OutboxProducer.Stage only compiles if the packed provider outbox staging extension resolved; its DDL
// source is pure string building, safe to run with no server.
Console.WriteLine($"outbox DDL bytes: {OutboxProducer.Ddl().Length}");
Console.WriteLine("package smoke OK");

namespace Smoke
{
    public sealed record Hello(string Name);

    public static class HelloJob
    {
        [Job("hello")]
        public static void Handle(Hello input) => Console.WriteLine($"hello {input.Name}");
    }
}
