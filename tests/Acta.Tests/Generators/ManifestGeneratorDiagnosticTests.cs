using Acta.Generators.Features.Jobs;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Acta.Tests.Generators;

/// <summary>
/// Drives <see cref="ActaManifestGenerator"/> through Roslyn over small in-memory compilations
/// and asserts the diagnostics it reports. The project references the generator as a plain project
/// (not an analyzer asset) exactly for this.
/// </summary>
public class ManifestGeneratorDiagnosticTests
{
    internal static GeneratorDriverRunResult RunGenerator(string source, string? rootNamespace = null)
    {
        var referencePaths = new HashSet<string>(
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator),
            StringComparer.OrdinalIgnoreCase
        )
        {
            typeof(JobAttribute).Assembly.Location,
        };

        var compilation = CSharpCompilation.Create(
            "GeneratorDiagnosticTests",
            [CSharpSyntaxTree.ParseText(source)],
            referencePaths.Select(p => MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var driver = CSharpGeneratorDriver.Create(
            generators: [new ActaManifestGenerator().AsSourceGenerator()],
            optionsProvider: new TestConfigOptionsProvider(rootNamespace)
        );

        return driver.RunGenerators(compilation).GetRunResult();
    }

    private static Diagnostic[] Of(GeneratorDriverRunResult result, string id) => [.. result.Diagnostics.Where(d => d.Id == id)];

    // ----------------------------------------------------------------------------------------
    // ACTA0101: duplicate job name
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Duplicate_job_name_errors_ACTA0101()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public sealed record InputOne(int Value);
            public sealed record InputTwo(int Value);

            public static class FirstHandler
            {
                [Job("dup")]
                public static void Run(InputOne input) { }
            }

            public static class SecondHandler
            {
                [Job("dup")]
                public static void Run(InputTwo input) { }
            }
            """
        );

        var errors = Of(result, "ACTA0101");
        Assert.Equal(2, errors.Length);
        Assert.All(errors, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
    }

    // ----------------------------------------------------------------------------------------
    // ACTA0102: invalid job name
    // ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Send_Email")]
    [InlineData("SendEmail")]
    [InlineData("send email")]
    [InlineData("-leading-dash")]
    [InlineData("trailing-dash-")]
    [InlineData("double--dash")]
    [InlineData("1-starts-with-digit")]
    public void Non_kebab_job_name_errors_ACTA0102(string jobName)
    {
        var result = RunGenerator(
            $$"""
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("{{jobName}}")]
                public static void Run() { }
            }
            """
        );

        var error = Assert.Single(Of(result, "ACTA0102"));
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
    }

    [Fact]
    public void Job_name_over_128_chars_errors_ACTA0102()
    {
        var longName = string.Join("-", Enumerable.Repeat("abcde", 26));
        var result = RunGenerator(
            $$"""
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("{{longName}}")]
                public static void Run() { }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0102"));
    }

    [Fact]
    public void System_prefix_outside_framework_assembly_errors_ACTA0102()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("sys.custom-sweep")]
                public static void Run() { }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0102"));
    }

    [Fact]
    public void System_prefix_inside_framework_assembly_is_valid()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace Acta.FrameworkJobs;

            public static class Handler
            {
                [Job("sys.custom-sweep")]
                public static void Run() { }
            }
            """,
            rootNamespace: "Acta.Runtime"
        );

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Kebab_job_name_is_valid()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("send-welcome-email-2")]
                public static void Run() { }
            }
            """
        );

        Assert.Empty(result.Diagnostics);
    }

    // ----------------------------------------------------------------------------------------
    // ACTA0103: invalid handler signature (all variants share the ID)
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Async_void_handler_errors_ACTA0103()
    {
        var result = RunGenerator(
            """
            using Acta;
            using System.Threading.Tasks;

            namespace GenTests;

            public sealed record Input(int Value);

            public static class Handler
            {
                [Job("async-void")]
                public static async void Run(Input input) => await Task.Yield();
            }
            """
        );

        Assert.Single(Of(result, "ACTA0103"));
    }

    [Fact]
    public void Extra_parameter_errors_ACTA0103()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public sealed record Input(int Value);

            public static class Handler
            {
                [Job("extra-param")]
                public static void Run(Input input, string other) { }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0103"));
    }

    [Fact]
    public void Invalid_parameter_order_errors_ACTA0103()
    {
        var result = RunGenerator(
            """
            using Acta;
            using System.Threading;
            using System.Threading.Tasks;

            namespace GenTests;

            public sealed record Input(int Value);

            public static class Handler
            {
                [Job("bad-order")]
                public static Task Run(Input input, CancellationToken ct, JobContext ctx) => Task.CompletedTask;
            }
            """
        );

        Assert.Single(Of(result, "ACTA0103"));
    }

    [Fact]
    public void Sync_handler_with_JobContext_errors_ACTA0103()
    {
        var result = RunGenerator(
            """
            using Acta;
            using System.Threading;

            namespace GenTests;

            public sealed record Input(int Value);

            public static class Handler
            {
                [Job("sync-with-context")]
                public static void Run(Input input, JobContext ctx, CancellationToken ct) { }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0103"));
    }

    [Fact]
    public void JobContext_without_CancellationToken_errors_ACTA0103()
    {
        var result = RunGenerator(
            """
            using Acta;
            using System.Threading.Tasks;

            namespace GenTests;

            public sealed record Input(int Value);

            public static class Handler
            {
                [Job("context-no-ct")]
                public static Task Run(Input input, JobContext ctx) => Task.CompletedTask;
            }
            """
        );

        Assert.Single(Of(result, "ACTA0103"));
    }

    [Fact]
    public void Private_handler_errors_ACTA0103()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public sealed record Input(int Value);

            public static class Handler
            {
                [Job("private-handler")]
                private static void Run(Input input) { }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0103"));
    }

    [Fact]
    public void Open_generic_handler_errors_ACTA0103()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("open-generic")]
                public static void Run<T>(T input) { }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0103"));
    }

    [Fact]
    public void Nested_awaitable_return_errors_ACTA0103()
    {
        var result = RunGenerator(
            """
            using Acta;
            using System.Threading.Tasks;

            namespace GenTests;

            public sealed record Input(int Value);

            public static class Handler
            {
                [Job("nested-awaitable")]
                public static Task<Task<int>> Run(Input input) => Task.FromResult(Task.FromResult(1));
            }
            """
        );

        Assert.Single(Of(result, "ACTA0103"));
    }

    [Fact]
    public void Forbidden_input_type_errors_ACTA0103()
    {
        var result = RunGenerator(
            """
            using Acta;
            using System;

            namespace GenTests;

            public static class Handler
            {
                [Job("forbidden-input")]
                public static void Run(IServiceProvider input) { }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0103"));
    }

    // ----------------------------------------------------------------------------------------
    // ACTA0104: duplicate input type (warning)
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Duplicate_input_type_warns_ACTA0104_and_keeps_both_jobs_in_the_manifest()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public sealed record SharedInput(int Value);

            public static class FirstHandler
            {
                [Job("first-job")]
                public static void Run(SharedInput input) { }
            }

            public static class SecondHandler
            {
                [Job("second-job")]
                public static void Run(SharedInput input) { }
            }
            """
        );

        var warnings = Of(result, "ACTA0104");
        Assert.Equal(2, warnings.Length);
        Assert.All(warnings, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));

        var manifest = Assert.Single(result.GeneratedTrees).ToString();
        Assert.Contains("first-job", manifest);
        Assert.Contains("second-job", manifest);
    }

    [Fact]
    public void Multiple_no_input_handlers_do_not_warn_ACTA0104()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class SweepHandler
            {
                [Job("sweep")]
                public static void Run() { }
            }

            public static class ReapHandler
            {
                [Job("reap")]
                public static void Run() { }
            }
            """
        );

        Assert.Empty(Of(result, "ACTA0104"));
    }

    // ----------------------------------------------------------------------------------------
    // ACTA0105: invalid policy value
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Malformed_duration_errors_ACTA0105()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("bad-duration", ExecutionTimeout = "10 minutes")]
                public static void Run() { }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0105"));
    }

    [Fact]
    public void Malformed_JobRetention_errors_ACTA0105()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("bad-retention", JobRetention = "junk")]
                public static void Run() { }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0105"));
    }

    [Fact]
    public void Non_positive_MaxAttempts_errors_ACTA0105()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("bad-attempts", MaxAttempts = 0)]
                public static void Run() { }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0105"));
    }

    [Fact]
    public void Backoff_multiplier_below_one_errors_ACTA0105()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("bad-multiplier", Backoff = "1s..2s x0.5")]
                public static void Run() { }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0105"));
    }

    [Fact]
    public void Backoff_jitter_outside_unit_interval_errors_ACTA0105()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("bad-jitter", Backoff = "1s..2s ±101%")]
                public static void Run() { }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0105"));
    }

    [Fact]
    public void Backoff_over_64_chars_errors_ACTA0105()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("bad-backoff-length", Backoff = "1s..2s exact exact exact exact exact exact exact exact exact exact exact")]
                public static void Run() { }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0105"));
    }

    [Fact]
    public void Backoff_huge_numeral_errors_ACTA0105_instead_of_crashing()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("huge-numeral", Backoff = "99999999999999999999d")]
                public static void Run() { }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0105"));
    }

    [Fact]
    public void Non_positive_RecurringResultCap_errors_ACTA0105()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("bad-cap", RecurringResultCap = 0)]
                public static void Run() { }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0105"));
    }

    [Fact]
    public void Undefined_enum_policy_value_errors_ACTA0105()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("bad-priority", Priority = (JobPriorityCode)42)]
                public static void Run() { }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0105"));
    }

    [Fact]
    public void Valid_policy_values_produce_no_diagnostics()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("good-policy", MaxAttempts = 3, Backoff = "1s..2m x2 ±20%",
                    ExecutionTimeout = "10m", Priority = JobPriorityCode.High)]
                public static void Run() { }
            }
            """
        );

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Iso_durations_are_accepted_without_diagnostics()
    {
        // ISO-8601 time-only durations are a first-class alternate spelling of the human syntax,
        // not a nudge-worthy variant; they compile silently.
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("iso-durations", Backoff = "PT1M..PT8H x2 ~10%", ExecutionTimeout = "PT30S")]
                public static void Run() { }
            }
            """
        );

        Assert.Empty(result.Diagnostics);
        Assert.Contains("iso-durations", Assert.Single(result.GeneratedTrees).ToString());
    }

    [Fact]
    public void Uppercase_human_duration_unit_errors_ACTA0142()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("bad-unit", Backoff = "1M")]
                public static void Run() { }
            }
            """
        );

        var error = Assert.Single(Of(result, "ACTA0142"));
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
    }

    [Fact]
    public void Unknown_week_unit_errors_ACTA0142()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("bad-week-unit", ExecutionTimeout = "2w")]
                public static void Run() { }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0142"));
    }

    [Fact]
    public void Day_duration_unit_is_valid()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("day-execution-timeout", ExecutionTimeout = "1d")]
                public static void Run() { }
            }
            """
        );

        Assert.Empty(result.Diagnostics);
        var manifest = Assert.Single(result.GeneratedTrees).ToString();
        Assert.Contains("ExecutionTimeoutSeconds = 86400,", manifest);
    }

    [Fact]
    public void JobRetention_duration_string_is_accepted()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("job-retention", JobRetention = "90d")]
                public static void Run() { }
            }
            """
        );

        Assert.Empty(result.Diagnostics);
        var manifest = Assert.Single(result.GeneratedTrees).ToString();
        Assert.Contains("JobRetentionSeconds = 7776000,", manifest);
    }

    // ----------------------------------------------------------------------------------------
    // ACTA0121: invalid schedule declaration
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void JobSchedule_without_Job_errors_ACTA0121()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [JobSchedule("orphan", Cron.Hourly)]
                public static void Run() { }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0121"));
    }

    [Fact]
    public void Duplicate_schedule_name_on_one_handler_errors_ACTA0121()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("dup-schedule")]
                [JobSchedule("tick", Cron.Hourly)]
                [JobSchedule("tick", Cron.Daily)]
                public static void Run() { }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0121"));
    }

    [Fact]
    public void Non_kebab_schedule_name_errors_ACTA0121()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("bad-schedule-name")]
                [JobSchedule("Every Morning", Cron.Daily)]
                public static void Run() { }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0121"));
    }

    [Fact]
    public void Blank_schedule_expression_errors_ACTA0121()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("blank-expression")]
                [JobSchedule("tick", "   ")]
                public static void Run() { }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0121"));
    }

    [Fact]
    public void Blank_environment_entry_errors_ACTA0121()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("blank-environment")]
                [JobSchedule("tick", Cron.Hourly, Environments = new[] { "production", " " })]
                public static void Run() { }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0121"));
    }

    // ----------------------------------------------------------------------------------------
    // ACTA0122: invalid schedule expression
    // ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("nonsense")]
    [InlineData("* * *")]
    [InlineData("99 * * * *")]
    [InlineData("* 25 * * *")]
    [InlineData("* * 32 * *")]
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 8")]
    [InlineData("*/0 * * * *")]
    [InlineData("@nonsense")]
    public void Invalid_cron_expression_errors_ACTA0122(string expression)
    {
        var result = RunGenerator(
            $$"""
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("bad-cron")]
                [JobSchedule("tick", "{{expression}}")]
                public static void Run() { }
            }
            """
        );

        var error = Assert.Single(Of(result, "ACTA0122"));
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
    }

    [Theory]
    [InlineData("PT-5M")]
    [InlineData("PT0S")]
    [InlineData("P")]
    [InlineData("5x")]
    [InlineData("0s")]
    public void Invalid_interval_errors_ACTA0122(string expression)
    {
        var result = RunGenerator(
            $$"""
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("bad-interval")]
                [JobSchedule("tick", "{{expression}}")]
                public static void Run() { }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0122"));
    }

    [Theory]
    [InlineData("0 8 * * *")]
    [InlineData("0 0 * * MON-FRI")]
    [InlineData("0 0 1 JAN *")]
    [InlineData("*/15 * * * * *")]
    [InlineData("0 9-17/2 * * 1-5")]
    [InlineData("0 0 L * *")]
    [InlineData("0 0 * * 1#2")]
    [InlineData("0 0 ? * 1")]
    [InlineData("@daily")]
    [InlineData("PT5M")]
    [InlineData("P1D")]
    [InlineData("5m")]
    [InlineData("10s")]
    [InlineData("1.5h")]
    public void Valid_schedule_expressions_produce_no_diagnostics(string expression)
    {
        var result = RunGenerator(
            $$"""
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("good-cron")]
                [JobSchedule("tick", "{{expression}}")]
                public static void Run() { }
            }
            """
        );

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Every_Cron_constant_passes_expression_validation()
    {
        foreach (var field in typeof(Cron).GetFields().Where(f => f.IsLiteral))
        {
            var expression = (string)field.GetRawConstantValue()!;
            var result = RunGenerator(
                $$"""
                using Acta;
                namespace GenTests;

                public static class Handler
                {
                    [Job("cron-constant")]
                    [JobSchedule("tick", "{{expression}}")]
                    public static void Run() { }
                }
                """
            );

            Assert.True(
                result.Diagnostics.IsEmpty,
                $"Cron.{field.Name} (\"{expression}\") raised {string.Join(", ", result.Diagnostics.Select(d => d.Id))}"
            );
        }
    }

    // ----------------------------------------------------------------------------------------
    // ACTA0123: scheduled input not constructible
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Scheduled_input_without_parameterless_ctor_errors_ACTA0123()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public sealed record Input(int Value);

            public static class Handler
            {
                [Job("scheduled-bad-input")]
                [JobSchedule("tick", Cron.Hourly)]
                public static void Run(Input input) { }
            }
            """
        );

        Assert.Single(Of(result, "ACTA0123"));
    }

    // ----------------------------------------------------------------------------------------
    // ACTA0131: invalid payload format declaration
    // ----------------------------------------------------------------------------------------

    private const string SerializerBody = """
        public JobPayloadFormat Format => default;
        public JobPayload Serialize<T>(T value) => default!;
        public T Deserialize<T>(JobPayload payload) => default!;
        """;

    [Fact]
    public void Payload_format_id_below_128_errors_ACTA0131()
    {
        var result = RunGenerator(
            $$"""
            using Acta;
            namespace GenTests;

            [JobPayloadFormatDeclaration(5, "my-format")]
            public sealed class MyFormat : IJobPayloadSerializer
            {
                {{SerializerBody}}
            }
            """
        );

        Assert.Single(Of(result, "ACTA0131"));
    }

    [Fact]
    public void Non_kebab_payload_format_name_errors_ACTA0131()
    {
        var result = RunGenerator(
            $$"""
            using Acta;
            namespace GenTests;

            [JobPayloadFormatDeclaration(130, "MyFormat")]
            public sealed class MyFormat : IJobPayloadSerializer
            {
                {{SerializerBody}}
            }
            """
        );

        Assert.Single(Of(result, "ACTA0131"));
    }

    [Fact]
    public void Builtin_payload_format_name_errors_ACTA0131()
    {
        var result = RunGenerator(
            $$"""
            using Acta;
            namespace GenTests;

            [JobPayloadFormatDeclaration(130, "json")]
            public sealed class MyFormat : IJobPayloadSerializer
            {
                {{SerializerBody}}
            }
            """
        );

        Assert.Single(Of(result, "ACTA0131"));
    }

    [Fact]
    public void Duplicate_payload_format_id_errors_ACTA0131()
    {
        var result = RunGenerator(
            $$"""
            using Acta;
            namespace GenTests;

            [JobPayloadFormatDeclaration(130, "format-one")]
            public sealed class FormatOne : IJobPayloadSerializer
            {
                {{SerializerBody}}
            }

            [JobPayloadFormatDeclaration(130, "format-two")]
            public sealed class FormatTwo : IJobPayloadSerializer
            {
                {{SerializerBody}}
            }
            """
        );

        Assert.Equal(2, Of(result, "ACTA0131").Length);
    }

    [Fact]
    public void Payload_format_not_implementing_serializer_errors_ACTA0131()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            [JobPayloadFormatDeclaration(130, "my-format")]
            public sealed class MyFormat
            {
            }
            """
        );

        Assert.Single(Of(result, "ACTA0131"));
    }

    [Fact]
    public void Valid_payload_format_declaration_produces_no_diagnostics()
    {
        var result = RunGenerator(
            $$"""
            using Acta;
            namespace GenTests;

            [JobPayloadFormatDeclaration(130, "json-gzip")]
            public sealed class JsonGzip : IJobPayloadSerializer
            {
                {{SerializerBody}}
            }
            """
        );

        Assert.Empty(result.Diagnostics);
    }

    // ----------------------------------------------------------------------------------------
    // ACTA0132: invalid [Job] payload-format usage
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Format_shorthand_is_valid_no_diagnostics()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public sealed record Input(int Value);
            public sealed record Output(int Value);

            public static class Handler
            {
                [Job("echo", Format = "json")]
                public static System.Threading.Tasks.Task<Output> Handle(Input input) => null!;
            }
            """
        );

        Assert.Empty(Of(result, "ACTA0132"));
    }

    [Fact]
    public void Asymmetric_input_and_output_formats_are_valid_no_diagnostics()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public sealed record Input(int Value);
            public sealed record Output(int Value);

            public static class Handler
            {
                [Job("echo", InputFormat = "json", OutputFormat = "text")]
                public static System.Threading.Tasks.Task<Output> Handle(Input input) => null!;
            }
            """
        );

        Assert.Empty(Of(result, "ACTA0132"));
    }

    [Fact]
    public void Format_combined_with_input_format_errors_ACTA0132()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public sealed record Input(int Value);
            public sealed record Output(int Value);

            public static class Handler
            {
                [Job("echo", Format = "json", InputFormat = "text")]
                public static System.Threading.Tasks.Task<Output> Handle(Input input) => null!;
            }
            """
        );

        Assert.Single(Of(result, "ACTA0132"));
    }

    [Fact]
    public void Format_combined_with_output_format_errors_ACTA0132()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public sealed record Input(int Value);
            public sealed record Output(int Value);

            public static class Handler
            {
                [Job("echo", Format = "json", OutputFormat = "text")]
                public static System.Threading.Tasks.Task<Output> Handle(Input input) => null!;
            }
            """
        );

        Assert.Single(Of(result, "ACTA0132"));
    }

    [Fact]
    public void Output_format_on_void_handler_errors_ACTA0132()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public sealed record Input(int Value);

            public static class Handler
            {
                [Job("echo", OutputFormat = "json")]
                public static System.Threading.Tasks.Task Handle(Input input) => null!;
            }
            """
        );

        Assert.Single(Of(result, "ACTA0132"));
    }

    [Fact]
    public void Unknown_format_name_errors_ACTA0132()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public sealed record Input(int Value);
            public sealed record Output(int Value);

            public static class Handler
            {
                [Job("echo", InputFormat = "made-up")]
                public static System.Threading.Tasks.Task<Output> Handle(Input input) => null!;
            }
            """
        );

        Assert.Single(Of(result, "ACTA0132"));
    }

    [Fact]
    public void Declared_custom_format_name_is_valid_no_diagnostics()
    {
        var result = RunGenerator(
            $$"""
            using Acta;
            namespace GenTests;

            public sealed record Input(int Value);
            public sealed record Output(int Value);

            [JobPayloadFormatDeclaration(130, "json-gzip")]
            public sealed class JsonGzip : IJobPayloadSerializer
            {
                {{SerializerBody}}
            }

            public static class Handler
            {
                [Job("echo", Format = "json-gzip")]
                public static System.Threading.Tasks.Task<Output> Handle(Input input) => null!;
            }
            """
        );

        Assert.Empty(Of(result, "ACTA0132"));
    }

    // ----------------------------------------------------------------------------------------
    // ACTA0106: contract member name collision (separators removed, case-insensitive)
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Contract_member_collision_warns_ACTA0106()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handlers
            {
                [Job("send-mail")]
                public static void A() { }

                [Job("sendmail")]
                public static void B() { }
            }
            """,
            rootNamespace: "GenTests"
        );

        var warnings = Of(result, "ACTA0106");
        Assert.Equal(2, warnings.Length);
        Assert.All(warnings, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
    }

    // ----------------------------------------------------------------------------------------
    // MisfireStrategy default: an unset [JobSchedule] misfire emits Skip (forward-only, drop missed),
    // matching the production scheduler's default; FireOnceCatchUp is opt-in.
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void JobSchedule_without_Misfire_defaults_to_Skip()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("daily-report")]
                [JobSchedule("nightly", Cron.Daily)]
                public static void Run() { }
            }
            """
        );

        var manifest = Assert.Single(result.GeneratedTrees).ToString();
        Assert.Contains("MisfireStrategy: MisfireStrategyCode.Skip", manifest);
        Assert.DoesNotContain("MisfireStrategyCode.FireOnceCatchUp", manifest);
    }

    [Fact]
    public void JobSchedule_without_TimeZone_emits_UTC()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("daily-report")]
                [JobSchedule("nightly", Cron.Daily)]
                public static void Run() { }
            }
            """
        );

        var manifest = Assert.Single(result.GeneratedTrees).ToString();
        Assert.Contains("TimeZoneId: \"UTC\"", manifest);
    }

    [Fact]
    public void JobSchedule_with_explicit_FireOnceCatchUp_is_emitted()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("catch-up-job")]
                [JobSchedule("nightly", Cron.Daily, MisfireStrategy = MisfireStrategyCode.FireOnceCatchUp)]
                public static void Run() { }
            }
            """
        );

        var manifest = Assert.Single(result.GeneratedTrees).ToString();
        Assert.Contains("MisfireStrategy: MisfireStrategyCode.FireOnceCatchUp", manifest);
    }

    // ----------------------------------------------------------------------------------------
    // Deadline policy: [Job] Deadline and DeadlineBehavior flow through to the emitted descriptor
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Deadline_and_behavior_are_emitted_in_descriptor()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("deadline-gen-probe", Deadline = "5h", DeadlineBehavior = DeadlineBehaviorCode.Advisory)]
                public static void Run() { }
            }
            """
        );

        Assert.Empty(result.Diagnostics);
        var manifest = Assert.Single(result.GeneratedTrees).ToString();
        Assert.Contains("DeadlineSeconds = 18000,", manifest);
        Assert.Contains("DeadlineBehavior = (Acta.DeadlineBehaviorCode)20,", manifest);
    }

    [Fact]
    public void Default_Strict_behavior_is_not_emitted_when_no_deadline_is_set()
    {
        var result = RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("no-deadline-probe")]
                public static void Run() { }
            }
            """
        );

        Assert.Empty(result.Diagnostics);
        var manifest = Assert.Single(result.GeneratedTrees).ToString();
        Assert.DoesNotContain("DeadlineSeconds", manifest);
        Assert.DoesNotContain("DeadlineBehavior", manifest);
    }
}

/// <summary>
/// Minimal options provider so tests can set <c>build_property.RootNamespace</c>.
/// </summary>
internal sealed class TestConfigOptionsProvider(string? rootNamespace) : AnalyzerConfigOptionsProvider
{
    public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(rootNamespace);

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;

    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;

    private sealed class Options(string? rootNamespace) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            if (key == "build_property.RootNamespace" && rootNamespace is not null)
            {
                value = rootNamespace;
                return true;
            }
            value = null!;
            return false;
        }
    }
}
