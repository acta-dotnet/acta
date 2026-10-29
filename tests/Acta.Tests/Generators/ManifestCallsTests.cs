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
                    JobHandle shipped = await jobs.EnqueueAsync(GenTestsJobs.ShipOrder(new ShipOrder(1)), o => o.DeduplicationKey("ship-1"));
                    JobHandle<int> cleaned = await jobs.EnqueueAsync(GenTestsJobs.Cleanup());
                    EnqueueJobResponse inTx = await jobs.EnqueueAsync(tx, GenTestsJobs.Reconcile());
                    EnqueueJobResponse configuredInTx = await jobs.EnqueueAsync(tx, GenTestsJobs.Cleanup(), o => o.CorrelationKey("c-1"));
                    EnqueueJobResponse inDerived = await jobs.EnqueueAsync(derived, GenTestsJobs.ShipOrder(new ShipOrder(4)), o => o.Lane("ships"));
                    JobOutcome<Receipt> waited = await jobs.RunAndWaitAsync(GenTestsJobs.SendInvoice(Guid.Empty, null));
                    JobOutcome ran = await jobs.RunAndWaitAsync(GenTestsJobs.ShipOrder(new ShipOrder(2)));
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
            .Where(m => m.Name is "EnqueueAsync" or "RunAndWaitAsync" or "ScheduleAsync")
            .ToList();
        Assert.Equal(11, verbs.Count);
        Assert.All(verbs, m => Assert.Contains(m.Parameters, p => p.Type.OriginalDefinition.Name == "JobCall"));
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
}
