using Xunit;

namespace Acta.Tests.Generators;

/// <summary>
/// Covers the compile-time <c>JobDescriptor.InputTemplateJson</c> skeleton: the enqueue form's shape
/// hint. Asserts the emitted literal, so the escaping and the member/value rules are both pinned.
/// </summary>
public class ManifestInputTemplateTests
{
    private static string Generate(string source) =>
        string.Concat(
            ManifestGeneratorDiagnosticTests.RunGenerator(source).Results.Single().GeneratedSources.Select(s => s.SourceText.ToString())
        );

    // The emitted line is a C# string literal; compare against the escaped form of the expected JSON.
    private static void AssertTemplate(string generated, string expectedJson) =>
        Assert.Contains($"InputTemplateJson = \"{expectedJson.Replace("\"", "\\\"")}\",", generated);

    [Fact]
    public void Record_primary_constructor_parameters_become_the_skeleton()
    {
        var generated = Generate(
            """
            using Acta;
            namespace GenTests;

            public sealed record AddNumbers(int Left, decimal Right, string Label, bool Verbose, System.Guid RequestId);

            public static class Handler
            {
                [Job("add-numbers")]
                public static void Run(AddNumbers input) { }
            }
            """
        );

        AssertTemplate(generated, """{"left":0,"right":0,"label":null,"verbose":false,"requestId":null}""");
    }

    [Fact]
    public void Init_properties_nest_objects_and_flatten_collections()
    {
        var generated = Generate(
            """
            using Acta;
            using System.Collections.Generic;
            namespace GenTests;

            public sealed class Address
            {
                public string City { get; set; }
                public int Zip { get; set; }
            }

            public sealed class Order
            {
                public string Reference { get; init; }
                public Address ShipTo { get; init; }
                public List<string> Lines { get; init; }
                public string[] Notes { get; init; }
                public Dictionary<string, string> Metadata { get; init; }
                public int Ignored { get; }
            }

            public static class Handler
            {
                [Job("place-order")]
                public static void Run(Order input) { }
            }
            """
        );

        AssertTemplate(generated, """{"reference":null,"shipTo":{"city":null,"zip":0},"lines":[],"notes":[],"metadata":{}}""");
    }

    [Fact]
    public void No_input_job_emits_no_template()
    {
        var generated = Generate(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("sweep")]
                public static void Run() { }
            }
            """
        );

        Assert.DoesNotContain("InputTemplateJson", generated);
    }

    [Fact]
    public void Non_json_input_format_emits_no_template()
    {
        var generated = Generate(
            """
            using Acta;
            namespace GenTests;

            public sealed record Message(string Body);

            public static class Handler
            {
                [Job("relay", InputFormat = "text")]
                public static void Run(Message input) { }
            }
            """
        );

        Assert.DoesNotContain("InputTemplateJson", generated);
    }

    [Fact]
    public void Self_referencing_type_emits_null_instead_of_recursing()
    {
        var generated = Generate(
            """
            using Acta;
            namespace GenTests;

            public sealed class Node
            {
                public string Name { get; set; }
                public Node Next { get; set; }
            }

            public static class Handler
            {
                [Job("walk")]
                public static void Run(Node input) { }
            }
            """
        );

        AssertTemplate(generated, """{"name":null,"next":null}""");
    }

    [Fact]
    public void Nesting_beyond_the_depth_bound_emits_null()
    {
        var generated = Generate(
            """
            using Acta;
            namespace GenTests;

            public sealed class Four { public int Value { get; set; } }
            public sealed class Three { public Four Next { get; set; } }
            public sealed class Two { public Three Next { get; set; } }
            public sealed class One { public Two Next { get; set; } }

            public static class Handler
            {
                [Job("deep")]
                public static void Run(One input) { }
            }
            """
        );

        AssertTemplate(generated, """{"next":{"next":{"next":null}}}""");
    }
}
