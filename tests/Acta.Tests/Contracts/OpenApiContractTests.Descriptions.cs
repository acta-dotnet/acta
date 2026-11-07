using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace Acta.Tests.Contracts;

public sealed partial class OpenApiContractTests
{
    private const int MaxDescriptionLength = 400;

    /// <summary>
    /// Every schema, property, parameter, response, and tag tells a client author what it is, in wire terms: a
    /// description that ends with a full stop, runs at most 400 characters, leaks no C#, and is no framework default.
    /// </summary>
    [Fact]
    public async Task Every_schema_property_parameter_response_and_tag_is_described()
    {
        var (items, problems) = DescriptionProblems(JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!);

        Assert.True(items > 1000, $"only {items} items; the walk is broken.");
        Assert.True(
            problems.Count == 0,
            "Describe these in wire terms:\n"
                + string.Join("\n", problems.Select(problem => $"{problem.Key}: {problem.Value}").Order(StringComparer.Ordinal))
        );
    }

    /// <summary>
    /// Each item of the contract by its key, and why each failing one fails. A code's description is judged without the
    /// known values the generator appends, and each known value's meaning is an item of its own.
    /// </summary>
    private static (int Items, Dictionary<string, string> Problems) DescriptionProblems(JsonNode document)
    {
        var schemas = document["components"]!["schemas"]!.AsObject();
        var schemaNames = schemas.Select(schema => schema.Key).ToHashSet(StringComparer.Ordinal);
        var descriptions = new Dictionary<string, string?>(StringComparer.Ordinal);
        var defaults = new HashSet<string>(StringComparer.Ordinal);
        var wireNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, schema) in schemas)
        {
            var text = Text(schema?["description"]);
            var knownValues = schema?[ActaOpenApiDocument.KnownValuesExtension] is null
                ? -1
                : text?.IndexOf("Known values:", StringComparison.Ordinal) ?? -1;
            descriptions[name] = knownValues < 0 ? text : text![..knownValues].Trim();
            foreach (var line in knownValues < 0 ? [] : text![knownValues..].Split('\n'))
            {
                if (line.StartsWith("- ", StringComparison.Ordinal) && line.IndexOf(": ", StringComparison.Ordinal) is > 0 and var colon)
                {
                    descriptions[$"{name} value {line[2..colon]}"] = line[(colon + 2)..].Trim();
                }
            }
            foreach (var (member, property) in Members(schema?["properties"]))
            {
                descriptions[$"{name}.{member}"] = Text(property?["description"]);
                wireNames.Add(member);
            }
        }

        foreach (var (_, operations) in Members(document["paths"]))
        {
            foreach (var (_, operation) in Members(operations))
            {
                var id = operation!["operationId"]!.GetValue<string>();
                foreach (var entry in Elements(operation["parameters"]))
                {
                    var parameter = entry?["$ref"]?.GetValue<string>() is { } reference
                        ? document["components"]!["parameters"]![reference.Split('/')[^1]]!
                        : entry!;
                    var name = parameter["name"]!.GetValue<string>();
                    descriptions[$"{id} parameter {name}"] = Text(parameter["description"]);
                    wireNames.Add(name);
                }
                foreach (var (status, response) in Members(operation["responses"]))
                {
                    var key = $"{id} response {status}";
                    descriptions[key] = Text(response?["description"]);
                    if (descriptions[key] == ReasonPhrases.GetReasonPhrase(int.Parse(status, CultureInfo.InvariantCulture)))
                    {
                        defaults.Add(key);
                    }
                }
            }
        }

        foreach (var tag in Elements(document["tags"]))
        {
            descriptions[$"tag {tag!["name"]!.GetValue<string>()}"] = Text(tag["description"]);
        }

        var problems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (item, text) in descriptions)
        {
            var problem =
                string.IsNullOrWhiteSpace(text) ? "no description"
                : defaults.Contains(item) ? $"the framework's default '{text}'"
                : Leak(text, wireNames, schemaNames) is { } leak ? $"leaks C#: '{leak}'"
                : !text.EndsWith('.') ? "does not end with a full stop"
                : text.Length > MaxDescriptionLength ? $"{text.Length} characters, more than {MaxDescriptionLength}"
                : null;
            if (problem is not null)
            {
                problems[item] = problem;
            }
        }
        return (descriptions.Count, problems);

        static string? Text(JsonNode? node) => node?.GetValue<string>().Trim();
    }

    // A C# type keyword before a dotted name, a namespace, generic arity, a cref, a dotted type and member, or a member's
    // C# spelling where the wire spells it in camelCase; a schema's own name is wire vocabulary.
    private static string? Leak(string text, HashSet<string> wireNames, HashSet<string> schemaNames)
    {
        foreach (var pattern in new[] { KeywordBeforeDottedName(), GenericArity(), DottedTypeAndMember() })
        {
            if (pattern.Match(text) is { Success: true } match)
            {
                return match.Value;
            }
        }

        foreach (var marker in new[] { "System.", "cref" })
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                return marker;
            }
        }

        return PascalCaseWord()
            .Matches(text)
            .Select(match => match.Value)
            .FirstOrDefault(word => !schemaNames.Contains(word) && wireNames.Contains(char.ToLowerInvariant(word[0]) + word[1..]));
    }

    [GeneratedRegex(@"\b(?:bool|byte|sbyte|short|ushort|int|uint|long|ulong|float|double|decimal|char|string|object|void)\??\s+\w+\.\w")]
    private static partial Regex KeywordBeforeDottedName();

    [GeneratedRegex(@"\w<\w[\w, ?]*>")]
    private static partial Regex GenericArity();

    [GeneratedRegex(@"\b[A-Z]\w*[a-z]\w*\.[A-Z]\w*[a-z]\w*\b")]
    private static partial Regex DottedTypeAndMember();

    [GeneratedRegex(@"\b[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]*)+\b")]
    private static partial Regex PascalCaseWord();
}
