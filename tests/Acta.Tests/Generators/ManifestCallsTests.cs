using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using static Acta.Tests.Generators.ManifestGeneratorDiagnosticTests;

namespace Acta.Tests.Generators;

/// <summary>
/// Covers the manifest's generated call factories and the argument record of a handler taking several input parameters:
/// what the manifest emits, that it compiles, and that each call binds the call overloads of <see cref="IJobs"/>.
/// </summary>
public class ManifestCallsTests
{
    private const string Handlers = """
        #nullable enable
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Acta;
        namespace GenTests;

        public sealed record Receipt(string Number);
        public sealed record ShipOrder(int OrderId);
        public sealed record Reconcile;

        public static class Billing
        {
            [Job("send-invoice")]
            public static Task<Receipt> Send(Guid invoiceId, string? recipient, JobContext context, CancellationToken ct) =>
                Task.FromResult(new Receipt(recipient ?? invoiceId.ToString()));

            [Job("ship-order")]
            public static void Ship(ShipOrder order) { }

            [Job("reconcile")]
            public static void Run(Reconcile input) { }

            [Job("cleanup")]
            public static Task<int> Cleanup(CancellationToken ct) => Task.FromResult(0);
        }
        """;

    private static string Manifest(GeneratorDriverRunResult result) => Assert.Single(result.GeneratedTrees).ToString();

    [Fact]
    public void Several_input_parameters_become_one_generated_argument_record()
    {
        var result = RunGenerator(Handlers, "GenTests", out var compilation);

        Assert.Empty(result.Diagnostics);
        AssertCompilesCleanly(compilation);
        var manifest = Manifest(result);
        Assert.Contains("public sealed record SendInvoiceArgs(global::System.Guid invoiceId, string? recipient);", manifest);
        Assert.Contains("InputType: typeof(global::GenTests.GenTestsJobs.SendInvoiceArgs),", manifest);
        Assert.Contains("InputPayloadFormat: global::Acta.JobPayloadFormat.Json,", manifest);
        Assert.Contains("global::GenTests.Billing.Send(typedRequest.invoiceId, typedRequest.recipient, context, ct)", manifest);
        Assert.Contains("InputTemplateJson = \"{\\\"invoiceId\\\":null,\\\"recipient\\\":null}\",", manifest);
    }

    [Fact]
    public void The_manifest_has_one_call_factory_per_job()
    {
        var manifest = Manifest(RunGenerator(Handlers, "GenTests"));

        const string Invoice = "global::GenTests.GenTestsJobs.SendInvoiceArgs, global::GenTests.Receipt";
        Assert.Contains(
            $"public static global::Acta.JobCall<{Invoice}> SendInvoice(global::System.Guid invoiceId, string? recipient) =>",
            manifest
        );
        Assert.Contains(
            $"new(new global::Acta.JobContract<{Invoice}>(typeof(GenTestsJobs), \"send-invoice\"), "
                + "new global::GenTests.GenTestsJobs.SendInvoiceArgs(invoiceId, recipient));",
            manifest
        );
        Assert.Contains(
            "public static global::Acta.JobCall<global::GenTests.ShipOrder> ShipOrder(global::GenTests.ShipOrder order) =>",
            manifest
        );
        Assert.Contains("(typeof(GenTestsJobs), \"ship-order\"), order);", manifest);
        // An input that carries no data is fabricated, as dispatch does, so its call takes nothing.
        Assert.Contains("public static global::Acta.JobCall<global::GenTests.Reconcile> Reconcile() =>", manifest);
        Assert.Contains("(typeof(GenTestsJobs), \"reconcile\"), new global::GenTests.Reconcile());", manifest);
        Assert.Contains("public static global::Acta.JobCall<global::Acta.NoInput, int> Cleanup() =>", manifest);
        Assert.Contains("(typeof(GenTestsJobs), \"cleanup\"), new global::Acta.NoInput());", manifest);
        Assert.DoesNotContain("JobContract<global::GenTests.ShipOrder> ShipOrder {", manifest, StringComparison.Ordinal);
    }

    // Each call must bind its own overload: the input-typed overloads would take the call itself as an input and fail
    // only at run time, and the result-less ones return the same types, so the test reads the bound method.
    [Fact]
    public void Every_IJobs_verb_binds_its_call_overload()
    {
        var usage = """
            public abstract class DerivedTransaction : System.Data.Common.DbTransaction { }

            public static class Usage
            {
                public static async Task Use(IJobs jobs, System.Data.Common.DbTransaction tx, DerivedTransaction derived)
                {
                    JobHandle<Receipt> invoice = await jobs.EnqueueAsync(GenTestsJobs.SendInvoice(Guid.Empty, "billing@example.com"));
                    JobHandle shipped = await jobs.EnqueueAsync(GenTestsJobs.ShipOrder(new ShipOrder(1)), o => o.IdempotencyKey("ship-1"));
                    JobHandle<int> cleaned = await jobs.EnqueueAsync(GenTestsJobs.Cleanup());
                    EnqueueJobResponse inTx = await jobs.EnqueueAsync(tx, GenTestsJobs.Reconcile());
                    EnqueueJobResponse configuredInTx = await jobs.EnqueueAsync(tx, GenTestsJobs.Cleanup(), o => o.CorrelationKey("c-1"));
                    EnqueueJobResponse inDerived = await jobs.EnqueueAsync(derived, GenTestsJobs.ShipOrder(new ShipOrder(4)), o => o.Lane("ships"));
                    JobOutcome<Receipt> waited = await jobs.CallAsync(GenTestsJobs.SendInvoice(Guid.Empty, null));
                    JobOutcome ran = await jobs.CallAsync(GenTestsJobs.ShipOrder(new ShipOrder(2)));
                    JobHandle later = await jobs.ScheduleAsync(GenTestsJobs.Reconcile(), TimeSpan.FromMinutes(1));
                    JobHandle<Receipt> at = await jobs.ScheduleAsync(GenTestsJobs.SendInvoice(Guid.Empty, null), DateTimeOffset.UnixEpoch);
                    EnqueueJobResponse laterInTx = await jobs.ScheduleAsync(tx, GenTestsJobs.ShipOrder(new ShipOrder(3)), TimeSpan.Zero);
                }
            }
            """;
        RunGenerator(Handlers + "\n" + usage, "GenTests", out var compilation);

        AssertCompilesCleanly(compilation);
        var tree = compilation.SyntaxTrees.First();
        var model = compilation.GetSemanticModel(tree);
        var verbs = tree.GetRoot(TestContext.Current.CancellationToken)
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(i => model.GetSymbolInfo(i, TestContext.Current.CancellationToken).Symbol)
            .OfType<IMethodSymbol>()
            .Where(m => m.Name is "EnqueueAsync" or "CallAsync" or "ScheduleAsync")
            .ToList();
        Assert.Equal(11, verbs.Count);
        Assert.All(verbs, m => Assert.Contains(m.Parameters, p => p.Type.OriginalDefinition.Name == "JobCall"));
    }

    [Fact]
    public void An_optional_result_is_carried_as_the_type_it_holds_with_no_diagnostic()
    {
        // An optional result is declared on the handler and read with TryGetValue; the typed surface stays non-nullable.
        var result = RunGenerator(
            """
            #nullable enable
            using System.Threading;
            using System.Threading.Tasks;
            using Acta;
            namespace GenTests;

            public sealed record Customer(string Name);
            public sealed record FindCustomer(int Id);
            public sealed record CountOrders(int CustomerId);

            public static class Handler
            {
                [Job("find-customer")]
                public static Task<Customer?> Find(FindCustomer query, CancellationToken ct) => Task.FromResult<Customer?>(null);

                [Job("count-orders")]
                public static Task<int?> Count(CountOrders query, CancellationToken ct) => Task.FromResult<int?>(null);
            }

            public static class Usage
            {
                public static async Task Use(IJobs jobs)
                {
                    JobHandle<Customer> customer = await jobs.EnqueueAsync(GenTestsJobs.FindCustomer(new FindCustomer(1)));
                    JobOutcome<int> orders = await jobs.CallAsync(GenTestsJobs.CountOrders(new CountOrders(1)));
                }
            }
            """,
            "GenTests",
            out var compilation
        );

        Assert.Empty(result.Diagnostics);
        AssertCompilesCleanly(compilation);
        var manifest = Manifest(result);
        Assert.Contains("JobCall<global::GenTests.FindCustomer, global::GenTests.Customer> FindCustomer(", manifest);
        Assert.Contains("JobCall<global::GenTests.CountOrders, int> CountOrders(", manifest);
        Assert.Contains("ResultType: typeof(int),", manifest);
        Assert.DoesNotContain("int?", manifest, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ValueTask<int?>", "new ValueTask<int?>((int?)null)", "int")]
    [InlineData("int?", "null", "int")]
    [InlineData("Task<Point?>", "Task.FromResult<Point?>(null)", "global::GenTests.Point")]
    public void A_nullable_value_type_result_is_carried_as_its_value_type(string declared, string body, string carried)
    {
        // Every shape a handler declares a nullable value-type result in is carried as the value type.
        var result = RunGenerator(
            $$"""
            #nullable enable
            using System.Threading.Tasks;
            using Acta;
            namespace GenTests;

            public readonly record struct Point(int X, int Y);
            public sealed record Locate(int Id);

            public static class Handler
            {
                [Job("locate")]
                public static {{declared}} Run(Locate query) => {{body}};
            }
            """,
            "GenTests",
            out var compilation
        );

        Assert.Empty(result.Diagnostics);
        AssertCompilesCleanly(compilation);
        var manifest = Manifest(result);
        Assert.Contains($"JobCall<global::GenTests.Locate, {carried}> Locate(", manifest);
        Assert.Contains($"ResultType: typeof({carried}),", manifest);
        Assert.Contains($"serializer.Serialize<{carried}>(({carried})value!)", manifest);
    }

    [Fact]
    public void A_nullable_data_less_result_collapses_to_no_result()
    {
        // A nullable data-less struct is carried as the struct, which, carrying no information, collapses to no result as
        // the non-nullable declaration does: the job stores none and its call has no result type.
        var result = RunGenerator(
            """
            #nullable enable
            using System.Threading.Tasks;
            using Acta;
            namespace GenTests;

            public readonly record struct Acked;
            public sealed record Acknowledge(int Id);

            public static class Handler
            {
                [Job("acknowledge")]
                public static Task<Acked?> Run(Acknowledge request) => Task.FromResult<Acked?>(null);
            }
            """,
            "GenTests",
            out var compilation
        );

        Assert.Empty(result.Diagnostics);
        AssertCompilesCleanly(compilation);
        var manifest = Manifest(result);
        Assert.Contains("public static global::Acta.JobCall<global::GenTests.Acknowledge> Acknowledge(", manifest);
        Assert.Contains("ResultType: null,", manifest);
        Assert.Contains("SerializeResult: null", manifest);
    }

    [Fact]
    public void A_schedule_on_a_job_taking_several_parameters_errors_ACTA0123()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("nightly")]
                [Schedule("tick", Cron.Daily)]
                public static void Run(int day, string region) { }
            }
            """
        );

        Assert.Single(result.Diagnostics, d => d.Id == "ACTA0123");
    }

    [Fact]
    public void An_interface_among_several_parameters_errors_ACTA0103_but_a_collection_does_not()
    {
        var result = RunGenerator(
            """
            using System.Collections.Generic;
            using Acta;
            namespace GenTests;

            public interface IClock { }

            public static class Handler
            {
                [Job("stamp")]
                public static void Stamp(IReadOnlyList<string> lines, IClock clock) { }
            }
            """
        );

        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("ACTA0103", error.Id);
        Assert.Contains("clock", error.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public void A_bytes_format_on_a_job_taking_several_parameters_errors_ACTA0132()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("upload", InputFormat = "bytes")]
                public static void Upload(string name, byte[] content) { }
            }
            """
        );

        Assert.Single(result.Diagnostics, d => d.Id == "ACTA0132");
    }

    // A job named like another job's argument record collides with it: both lose their contract members and calls,
    // and the record takes a name no member can have, so the manifest still compiles.
    [Fact]
    public void An_argument_record_colliding_with_a_job_warns_ACTA0106_and_compiles()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("send-invoice")]
                public static void Send(int invoiceId, string recipient) { }

                [Job("send-invoice-args")]
                public static void SendArgs(int invoiceId) { }
            }
            """,
            "GenTests",
            out var compilation
        );

        Assert.Equal(2, result.Diagnostics.Count(d => d.Id == "ACTA0106"));
        AssertCompilesCleanly(compilation);
        var manifest = Manifest(result);
        Assert.Contains("public sealed record Args_0_send_invoice(int invoiceId, string recipient);", manifest);
        Assert.DoesNotContain("SendInvoice(", manifest, StringComparison.Ordinal);
    }

    // A public member that names an internal type does not compile (CS0050, CS0051), and an internal contract is an
    // ordinary choice for a job no other assembly calls.
    [Theory]
    [InlineData(
        "internal sealed record Order(int Id);",
        "internal static void Run(Order order) { }",
        "JobCall<global::GenTests.Order> OnlyJob("
    )]
    [InlineData("internal sealed record Receipt(int Id);", "public static Receipt Run(Query query) => new(query.Id);", "Receipt> OnlyJob(")]
    [InlineData("internal sealed record Part(int Id);", "public static void Run(Part part, int count) { }", "OnlyJobArgs> OnlyJob(")]
    [InlineData(
        "public static class Outer { internal sealed record Inner(int Id); }",
        "public static void Run(Outer.Inner inner) { }",
        "Inner> OnlyJob("
    )]
    [InlineData(
        "internal sealed record Item(int Id);",
        "public static void Run(System.Collections.Generic.List<Item[]> items) { }",
        "OnlyJob("
    )]
    public void A_job_with_an_internal_contract_type_gets_an_internal_call(string declaration, string handler, string call)
    {
        var result = RunGenerator(
            $$"""
            using Acta;
            namespace GenTests;

            public sealed record Query(int Id);
            public sealed record Probe(int Id);
            {{declaration}}

            internal static class Handler
            {
                [Job("only-job")]
                {{handler}}

                [Job("open-job")]
                public static Probe Open(Probe probe) => probe;
            }
            """,
            "GenTests",
            out var compilation
        );

        Assert.Empty(result.Diagnostics);
        AssertCompilesCleanly(compilation);
        var manifest = Manifest(result);
        var factory = manifest.Split('\n').Single(line => line.Contains(call, StringComparison.Ordinal));
        Assert.StartsWith("    internal static global::Acta.JobCall<", factory, StringComparison.Ordinal);
        if (call.Contains("Args", StringComparison.Ordinal))
        {
            Assert.Contains("internal sealed record OnlyJobArgs(", manifest, StringComparison.Ordinal);
        }

        // A job whose contract is public keeps its public call beside it, though its handler is internal too.
        Assert.Contains("public static global::Acta.JobCall<global::GenTests.Probe, global::GenTests.Probe> OpenJob(", manifest);
    }
}
