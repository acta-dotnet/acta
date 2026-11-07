using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Contracts;

/// <summary>
/// The calendar definition's schemas in the document are written by hand, since the compiler reads JSON and no record.
/// These facts hold them to <see cref="CalendarDefinition"/>: the same kinds, the same members on each kind, the same
/// required ones, so a client that builds a definition from the document builds one the server takes.
/// </summary>
public sealed partial class CalendarDefinitionSchemaTests
{
    private static readonly Lazy<Task<JsonObject>> Schemas = new(async () =>
        JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!["components"]!["schemas"]!.AsObject()
    );

    private const string Weekly = """
        {"kind":"weekly-availability","description":"Office hours.","timeZone":"Europe/Ljubljana",
         "weekly":[{"days":["Monday","Friday"],"windows":[["09:00","17:00"],["22:00","02:00"]]}],
         "dateOverrides":{"2027-01-01":[]},
         "excludedRanges":[{"from":"2027-08-01","to":"2027-08-15"},{"fromUtc":"2027-01-05T10:00:00Z","toUtc":"2027-01-05T11:00:00Z"}]}
        """;

    /// <summary>One definition per kind that uses every member of its schema, with the role it compiles in.</summary>
    private static readonly (string Kind, CalendarRoleCode Role, string Json)[] Cases =
    [
        ("weekly-availability", CalendarRoleCode.Availability, Weekly),
        ("union", CalendarRoleCode.Availability, $$"""{"kind":"union","description":"Either.","of":[{{Weekly}}]}"""),
        (
            "union",
            CalendarRoleCode.Occurrence,
            """{"kind":"union","description":"Either.","of":[{"kind":"explicit-instants","instants":["2027-01-15T16:00:00Z"]}]}"""
        ),
        ("intersection", CalendarRoleCode.Availability, $$"""{"kind":"intersection","description":"Both.","of":[{{Weekly}}]}"""),
        (
            "subtraction",
            CalendarRoleCode.Availability,
            $$"""{"kind":"subtraction","description":"Open but not closed.","from":{{Weekly}},"minus":{{Weekly}}}"""
        ),
        (
            "explicit-instants",
            CalendarRoleCode.Occurrence,
            """
            {"kind":"explicit-instants","description":"Launch days.","timeZone":"schedule",
             "instants":["2027-01-15T16:00:00Z","2027-01-15T18:00:00+02:00","2027-01-16T09:30"]}
            """
        ),
        (
            "daily-local-time",
            CalendarRoleCode.Occurrence,
            """
            {"kind":"daily-local-time","description":"Twice a day.","timeZone":"UTC","times":["09:00","17:30:15"],
             "days":["Monday","Tuesday"],"excludedDates":["2027-12-25"]}
            """
        ),
        (
            "business-day",
            CalendarRoleCode.Occurrence,
            """
            {"kind":"business-day","description":"Month end.","timeZone":"UTC","select":"last","of":"month","nth":2,
             "businessDays":["Monday","Tuesday","Wednesday"],"holidays":["2027-12-27"],"time":"08:00"}
            """
        ),
        (
            "at-opening",
            CalendarRoleCode.Occurrence,
            $$"""{"kind":"at-opening","description":"When the office opens.","availability":{{Weekly}}}"""
        ),
    ];

    public static TheoryData<string, CalendarRoleCode, string> Examples()
    {
        var examples = new TheoryData<string, CalendarRoleCode, string>();
        foreach (var (kind, role, json) in Cases)
        {
            examples.Add(kind, role, json);
        }
        return examples;
    }

    [Fact]
    public async Task Every_kind_the_compiler_knows_has_a_schema_and_no_other()
    {
        var schemas = await Schemas.Value;
        var kinds = CalendarDefinition
            .AvailabilityKinds.Concat(CalendarDefinition.OccurrenceKinds)
            .Distinct()
            .Order(StringComparer.Ordinal);
        var definition = schemas[ActaOpenApiDocument.CalendarDefinitionSchema]!;

        Assert.Equal(kinds, ActaOpenApiDocument.CalendarKindSchemas.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(
            ActaOpenApiDocument
                .CalendarKindSchemas.Select(kind => $"{kind.Key} #/components/schemas/{kind.Value}")
                .Order(StringComparer.Ordinal),
            definition["discriminator"]!["mapping"]!
                .AsObject()
                .Select(kind => $"{kind.Key} {kind.Value!.GetValue<string>()}")
                .Order(StringComparer.Ordinal)
        );
        Assert.Equal(
            ActaOpenApiDocument.CalendarKindSchemas.Values.Order(StringComparer.Ordinal),
            definition["oneOf"]!
                .AsArray()
                .Select(variant => variant!["$ref"]!.GetValue<string>().Split('/')[^1])
                .Order(StringComparer.Ordinal)
        );
        Assert.Equal(kinds, Cases.Select(example => example.Kind).Distinct().Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The names the compiler's source passes to its member checks are the names the schemas declare, across every kind and
    /// the two parts a weekly calendar nests. A member added to the compiler alone, or to a schema alone, fails here.
    /// </summary>
    [Fact]
    public async Task Every_member_the_compiler_reads_is_a_schema_member_and_no_other()
    {
        var schemas = await Schemas.Value;
        var source = File.ReadAllText(
            Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta", "Domain", "Calendars", "CalendarDefinition.cs")
        );
        var read = MemberChecks()
            .Matches(source)
            .SelectMany(check => Quoted().Matches(check.Value).Select(name => name.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        var declared = ActaOpenApiDocument
            .CalendarKindSchemas.Values.Concat(["CalendarWeeklyRule", "CalendarExcludedRange"])
            .SelectMany(name => schemas[name]!["properties"]!.AsObject().Select(member => member.Key))
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(read.Count > 20, $"only {read.Count} names read from the compiler's source; the scan is broken.");
        Assert.Equal(read.Order(StringComparer.Ordinal), declared.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public async Task A_kinds_schema_holds_what_the_compiler_takes(string kind, CalendarRoleCode role, string example)
    {
        var schemas = await Schemas.Value;
        var schema = schemas[ActaOpenApiDocument.CalendarKindSchemas[kind]]!;
        var members = schema["properties"]!.AsObject().Select(member => member.Key).ToHashSet(StringComparer.Ordinal);
        var required = schema["required"]!.AsArray().Select(member => member!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var others = ActaOpenApiDocument
            .CalendarKindSchemas.Values.SelectMany(name => schemas[name]!["properties"]!.AsObject().Select(member => member.Key))
            .Where(member => !members.Contains(member))
            .ToHashSet(StringComparer.Ordinal);
        var definition = JsonNode.Parse(example)!.AsObject();

        // The example is one the server takes, and it uses every member the schema declares.
        Assert.Empty(Problems(role, definition));
        Assert.Equal(members.Order(StringComparer.Ordinal), definition.Select(member => member.Key).Order(StringComparer.Ordinal));
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());

        foreach (var member in members.Where(member => member != "kind"))
        {
            var without = definition.DeepClone().AsObject();
            without.Remove(member);
            var missing = Problems(role, without).Contains($"$.{member}: required.");
            Assert.True(missing == required.Contains(member), $"{kind}.{member}: the schema's required and the compiler disagree.");
        }

        // A member of another kind is refused on this one, so the schema's additionalProperties: false is the server's rule.
        foreach (var member in others)
        {
            var with = definition.DeepClone().AsObject();
            with[member] = "x";
            Assert.Contains($"$.{member}: not a property of this kind.", Problems(role, with));
        }
    }

    private static IReadOnlyList<string> Problems(CalendarRoleCode role, JsonObject definition)
    {
        try
        {
            CalendarDefinition.Compile(role, definition.ToJsonString());
            return [];
        }
        catch (CalendarDefinitionException ex)
        {
            return ex.Problems;
        }
    }

    // A member check in the compiler's source, Known(element, path, "name", ...), and the list every kind shares.
    [GeneratedRegex("""Known\(\w+, \w+(?:, "[A-Za-z]+")+\)|CommonProperties = \[[^\]]+\]""")]
    private static partial Regex MemberChecks();

    [GeneratedRegex("\"([A-Za-z]+)\"")]
    private static partial Regex Quoted();
}
