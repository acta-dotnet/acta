using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Contracts;

/// <summary>
/// The calendar definition's schema in the document is written by hand, since the compiler reads JSON and no record.
/// These facts hold it to <see cref="CalendarDefinition"/>: the same kinds, every member some kind takes, and each member
/// naming the kinds that take it and saying when they require it, so a client that builds a definition from the document
/// builds one the server takes.
/// </summary>
public sealed partial class CalendarDefinitionSchemaTests
{
    private static readonly Lazy<Task<JsonObject>> Schemas = new(async () =>
        JsonNode.Parse(await ActaOpenApiDocument.GenerateAsync())!["components"]!["schemas"]!.AsObject()
    );

    private static readonly string[] Kinds =
    [
        .. CalendarDefinition.AvailabilityKinds.Concat(CalendarDefinition.OccurrenceKinds).Distinct().Order(StringComparer.Ordinal),
    ];

    private const string Weekly = """
        {"kind":"weekly-availability","description":"Office hours.","timeZone":"Europe/Ljubljana",
         "weekly":[{"days":["Monday","Friday"],"windows":[["09:00","17:00"],["22:00","02:00"]]}],
         "dateOverrides":{"2027-01-01":[]},
         "excludedRanges":[{"from":"2027-08-01","to":"2027-08-15"},{"fromUtc":"2027-01-05T10:00:00Z","toUtc":"2027-01-05T11:00:00Z"}]}
        """;

    /// <summary>One definition per kind that uses every member the kind takes, with the role it compiles in.</summary>
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

    /// <summary>
    /// The definition is one object with no oneOf to choose by, so a generated client reads and writes every kind through
    /// one type. Its kind is required and a plain string, since a one-value enum or a pattern on it breaks a generated C#
    /// client, and its description names every kind the compiler knows.
    /// </summary>
    [Fact]
    public async Task The_definition_is_one_object_whose_kind_names_every_kind()
    {
        var definition = (await Schemas.Value)[ActaOpenApiDocument.CalendarDefinitionSchema]!;
        var kind = definition["properties"]!["kind"]!;

        Assert.Equal("object", definition["type"]!.GetValue<string>());
        Assert.False(definition["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(["kind"], definition["required"]!.AsArray().Select(member => member!.GetValue<string>()));
        Assert.Null(definition["oneOf"]);
        Assert.Null(definition["discriminator"]);
        Assert.Equal(["description", "type"], kind.AsObject().Select(keyword => keyword.Key).Order(StringComparer.Ordinal));
        Assert.Equal("string", kind["type"]!.GetValue<string>());
        Assert.Equal(Kinds, KindsNamed(kind["description"]!.GetValue<string>()));
        Assert.Equal(Kinds, Cases.Select(example => example.Kind).Distinct().Order(StringComparer.Ordinal));
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
        var declared = new[] { ActaOpenApiDocument.CalendarDefinitionSchema, "CalendarWeeklyRule", "CalendarExcludedRange" }
            .SelectMany(name => schemas[name]!["properties"]!.AsObject().Select(member => member.Key))
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(read.Count > 20, $"only {read.Count} names read from the compiler's source; the scan is broken.");
        Assert.Equal(read.Order(StringComparer.Ordinal), declared.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A kind takes a member exactly when the member's description names the kind, and requires it exactly when the
    /// description ends in Required. Which members a kind takes is read from the compiler: a member is the kind's unless
    /// the compiler refuses it as not a property of this kind. Across every kind, each description names exactly the kinds
    /// that take its member.
    /// </summary>
    [Theory]
    [MemberData(nameof(Examples))]
    public async Task A_member_names_the_kinds_the_compiler_takes_it_on(string kind, CalendarRoleCode role, string example)
    {
        var properties = (await Schemas.Value)[ActaOpenApiDocument.CalendarDefinitionSchema]!["properties"]!.AsObject();
        var definition = JsonNode.Parse(example)!.AsObject();
        var taken = properties
            .Select(member => member.Key)
            .Where(member =>
                member != "kind" && !Problems(role, With(definition, member, "x")).Contains($"$.{member}: not a property of this kind.")
            )
            .ToHashSet(StringComparer.Ordinal);

        // The example is one the server takes, and it uses every member the kind takes.
        Assert.Empty(Problems(role, definition));
        Assert.Equal(
            taken.Order(StringComparer.Ordinal),
            definition.Select(member => member.Key).Where(member => member != "kind").Order(StringComparer.Ordinal)
        );

        foreach (var (member, schema) in properties.Where(member => member.Key != "kind"))
        {
            var description = schema!["description"]!.GetValue<string>();
            Assert.True(
                KindsNamed(description).Contains(kind) == taken.Contains(member),
                $"{member}: its description {(taken.Contains(member) ? "does not name" : "names")} {kind}, which "
                    + $"{(taken.Contains(member) ? "takes" : "refuses")} it."
            );
            if (taken.Contains(member))
            {
                var without = definition.DeepClone().AsObject();
                without.Remove(member);
                var required = Problems(role, without).Contains($"$.{member}: required.");
                Assert.True(
                    required == description.EndsWith("Required.", StringComparison.Ordinal),
                    $"{kind}.{member}: the compiler {(required ? "requires" : "does not require")} it, and its description disagrees."
                );
            }
        }
    }

    /// <summary>
    /// A definition of every kind, and unions and intersections nested in each other, in a subtraction, and in an
    /// at-opening, is a value the definition's schema allows under the contract tests' validator, which applies oneOf
    /// strictly.
    /// </summary>
    [Fact]
    public async Task A_definition_of_every_kind_is_a_value_its_schema_allows()
    {
        var schemas = await Schemas.Value;
        const string Closed = """{"kind":"weekly-availability","dateOverrides":{"2027-01-01":[]}}""";
        (string Kind, CalendarRoleCode Role, string Json)[] nested =
        [
            (
                "nested union",
                CalendarRoleCode.Availability,
                $$"""{"kind":"union","of":[{"kind":"intersection","of":[{{Closed}},{{Closed}}]},{{Closed}}]}"""
            ),
            (
                "nested at-opening",
                CalendarRoleCode.Occurrence,
                $$"""
                {"kind":"at-opening","availability":{"kind":"subtraction",
                 "from":{"kind":"intersection","of":[{{Closed}},{{Closed}}]},"minus":{"kind":"union","of":[{{Closed}}]} } }
                """
            ),
        ];
        var wrong = new List<string>();
        foreach (var (kind, role, json) in Cases.Concat(nested))
        {
            Assert.Empty(Problems(role, JsonNode.Parse(json)!.AsObject()));
            wrong.AddRange(
                OpenApiContractTests.Breaks(JsonNode.Parse(json), schemas[ActaOpenApiDocument.CalendarDefinitionSchema]!, schemas, kind)
            );
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    /// <summary>
    /// A weekly rule and an excluded range are no kinds: each takes the members its schema declares and no other, not
    /// even a kind or a note.
    /// </summary>
    [Fact]
    public async Task A_weekly_rule_and_an_excluded_range_take_only_their_own_members()
    {
        var schemas = await Schemas.Value;
        string[] rule = ["days", "windows"];
        string[] range = ["from", "fromUtc", "to", "toUtc"];
        Assert.Equal(
            rule,
            schemas["CalendarWeeklyRule"]!["properties"]!.AsObject().Select(member => member.Key).Order(StringComparer.Ordinal)
        );
        Assert.Equal(
            rule,
            schemas["CalendarWeeklyRule"]!["required"]!.AsArray().Select(member => member!.GetValue<string>()).Order(StringComparer.Ordinal)
        );
        Assert.Equal(
            range,
            schemas["CalendarExcludedRange"]!["properties"]!.AsObject().Select(member => member.Key).Order(StringComparer.Ordinal)
        );
        Assert.False(schemas["CalendarWeeklyRule"]!["additionalProperties"]!.GetValue<bool>());
        Assert.False(schemas["CalendarExcludedRange"]!["additionalProperties"]!.GetValue<bool>());

        foreach (var extra in new[] { "kind", "description", "timeZone" })
        {
            var inRule = JsonNode.Parse(Weekly)!.AsObject();
            inRule["weekly"]![0]![extra] = "x";
            Assert.Contains($"$.weekly[0].{extra}: not a property of a weekly rule.", Problems(CalendarRoleCode.Availability, inRule));
            var inDates = JsonNode.Parse(Weekly)!.AsObject();
            inDates["excludedRanges"]![0]![extra] = "x";
            Assert.Contains(
                $"$.excludedRanges[0].{extra}: not a property of an excluded range of dates.",
                Problems(CalendarRoleCode.Availability, inDates)
            );
            var inInstants = JsonNode.Parse(Weekly)!.AsObject();
            inInstants["excludedRanges"]![1]![extra] = "x";
            Assert.Contains(
                $"$.excludedRanges[1].{extra}: not a property of an excluded range of instants.",
                Problems(CalendarRoleCode.Availability, inInstants)
            );
        }

        foreach (var member in rule)
        {
            var without = JsonNode.Parse(Weekly)!.AsObject();
            without["weekly"]![0]!.AsObject().Remove(member);
            Assert.Contains($"$.weekly[0].{member}: required.", Problems(CalendarRoleCode.Availability, without));
        }
    }

    // The kinds a description names: each kind written as a whole word, or all of them where it says every kind.
    private static string[] KindsNamed(string description) =>
        description.Contains("every kind", StringComparison.Ordinal)
            ? Kinds
            : [.. Kinds.Where(kind => Regex.IsMatch(description, $"(?<![\\w-]){Regex.Escape(kind)}(?![\\w-])"))];

    private static JsonObject With(JsonObject definition, string member, string value)
    {
        var with = definition.DeepClone().AsObject();
        with[member] = value;
        return with;
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

    // A member check in the compiler's source, Known(element, path, "name", ...) or Only(element, path, "owner", "name", ...),
    // and the list every kind shares. An owner is a phrase, so the name pattern below passes over it.
    [GeneratedRegex("""(?:Known|Only)\(\w+, \w+(?:, "[^"]+")+\)|CommonProperties = \[[^\]]+\]""")]
    private static partial Regex MemberChecks();

    [GeneratedRegex("\"([A-Za-z]+)\"")]
    private static partial Regex Quoted();
}
