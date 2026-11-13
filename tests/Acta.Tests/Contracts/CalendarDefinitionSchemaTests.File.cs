using System.Text.Json;
using System.Text.Json.Nodes;
using Acta.Tests.Docs;
using Json.Schema;
using Xunit;

namespace Acta.Tests.Contracts;

// The published JSON Schema of a calendar definition is a second description of what CalendarDefinition compiles. These
// facts hold the two together over one set of definitions: what compiles passes the schema, what the schema refuses does
// not compile, and the rules that need more than shape are listed, so the schema's stated limit is a tested one. The
// draft request of the HTTP API is a third description, held to the same accepted definitions.
public sealed partial class CalendarDefinitionSchemaTests
{
    private const string Daily = """{"kind":"daily-local-time","times":["09:00"]}""";

    private static readonly Lazy<JsonSchema> Published = new(() => JsonSchema.FromText(File.ReadAllText(SchemaFile("docs", "reference"))));

    // The draft request as a JSON Schema of its own: the document's component schemas under $defs and every reference
    // pointed there, so the validator that reads the published file reads the request too.
    private static readonly Lazy<Task<JsonSchema>> DraftRequest = new(async () =>
        JsonSchema.FromText(
            $$"""
            {"$schema":"https://json-schema.org/draft/2020-12/schema","$ref":"#/$defs/SaveCalendarDraftRequest",
             "$defs":{{(await Schemas.Value).ToJsonString().Replace("#/components/schemas/", "#/$defs/", StringComparison.Ordinal)}}}
            """
        )
    );

    public static TheoryData<string> Accepted() =>
        [
            .. Cases.Select(c => c.Json),
            .. Cases.Select(c =>
                c.Json.Insert(c.Json.IndexOf('{') + 1, "\"$schema\":\"https://useacta.net/schemas/calendar-definition-v1.json\",")
            ),
            """{"kind":"daily-local-time","times":["09:00"],"days":["monday","FRIDAY","Sunday"]}""",
            """{"kind":"weekly-availability","weekly":[{"days":["Saturday"],"windows":[["00:00","24:00"],["22:00","02:00"]]}]}""",
            """{"kind":"weekly-availability","dateOverrides":{"2027-01-01":[["10:00","12:00"]]}}""",
            """{"kind":"weekly-availability","weekly":[],"dateOverrides":{"2027-01-01":[]}}""",
            """{"kind":"explicit-instants","instants":["2027-01-15T16:00:00Z","2027-01-15T16:00:00-05:00","2027-01-15T16:00","2027-01-15T16:00:30"]}""",
            """{"kind":"business-day","select":"first","period":"quarter","time":"09:00:30"}""",
            // A whole number however JSON spells it, and a description measured in characters, not UTF-16 units.
            """{"kind":"business-day","select":"first","period":"month","nth":2.0,"time":"09:00"}""",
            """{"kind":"business-day","select":"first","period":"month","nth":1e0,"time":"09:00"}""",
            $$"""{"kind":"daily-local-time","times":["09:00"],"description":"{{string.Concat(Enumerable.Repeat("\U0001F600", 512))}}"}""",
        ];

    public static TheoryData<string> Refused() =>
        [
            "[1,2]",
            """{"description":"no kind"}""",
            """{"kind":"nonsense"}""",
            """{"kind":"daily-local-time"}""",
            """{"kind":"daily-local-time","times":[]}""",
            """{"kind":"daily-local-time","times":["25:00"]}""",
            """{"kind":"daily-local-time","times":["9:00"]}""",
            """{"kind":"daily-local-time","times":["09:00"],"days":[]}""",
            """{"kind":"daily-local-time","times":["09:00"],"days":["Funday"]}""",
            """{"kind":"daily-local-time","times":["09:00"],"days":["1"]}""",
            """{"kind":"daily-local-time","times":["09:00"],"excludedDates":["2027-13-01"]}""",
            """{"kind":"daily-local-time","times":["09:00"],"weekly":[]}""",
            """{"kind":"daily-local-time","times":["09:00"],"description":5}""",
            """{"kind":"daily-local-time","times":["09:00"],"timeZone":""}""",
            """{"$schema":5,"kind":"daily-local-time","times":["09:00"]}""",
            $$"""{"kind":"daily-local-time","times":["09:00"],"description":"{{new string('x', 513)}}"}""",
            """{"kind":"business-day","select":"last","of":"month","time":"16:00"}""",
            """{"kind":"business-day","select":"middle","period":"month","time":"16:00"}""",
            """{"kind":"business-day","select":"first","period":"decade","time":"16:00"}""",
            """{"kind":"business-day","select":"first","period":"month","nth":0,"time":"16:00"}""",
            """{"kind":"business-day","select":"first","period":"month","nth":32,"time":"16:00"}""",
            """{"kind":"business-day","select":"first","period":"month","nth":1.5,"time":"16:00"}""",
            $$"""{"kind":"daily-local-time","times":["09:00"],"description":"{{string.Concat(Enumerable.Repeat("\U0001F600", 513))}}"}""",
            """{"kind":"business-day","select":"first","period":"month"}""",
            """{"kind":"explicit-instants","instants":[]}""",
            """{"kind":"explicit-instants","instants":["yesterday"]}""",
            """{"kind":"explicit-instants","instants":[5]}""",
            """{"kind":"at-opening"}""",
            $$"""{"kind":"at-opening","availability":{{Daily}}}""",
            $$"""{"kind":"at-opening","timeZone":"UTC","availability":{{Weekly}}}""",
            """{"kind":"union"}""",
            """{"kind":"union","of":[]}""",
            $$"""{"kind":"union","of":[{{Daily}},{{Weekly}}]}""",
            """{"kind":"union","of":[{"$schema":"x","kind":"daily-local-time","times":["09:00"]}]}""",
            $$"""{"kind":"union","timeZone":"UTC","of":[{{Daily}}]}""",
            $$"""{"kind":"intersection","of":[{{Daily}}]}""",
            $$"""{"kind":"subtraction","from":{{Weekly}}}""",
            """{"kind":"weekly-availability"}""",
            """{"kind":"weekly-availability","weekly":[]}""",
            """{"kind":"weekly-availability","dateOverrides":{}}""",
            """{"kind":"weekly-availability","dateOverrides":[]}""",
            """{"kind":"weekly-availability","dateOverrides":{"January 1":[]}}""",
            """{"kind":"weekly-availability","weekly":[{"days":["Monday"]}]}""",
            """{"kind":"weekly-availability","weekly":[{"days":["Monday"],"windows":[]}]}""",
            """{"kind":"weekly-availability","weekly":[{"days":[],"windows":[["09:00","17:00"]]}]}""",
            """{"kind":"weekly-availability","weekly":[{"days":["Monday"],"windows":[["09:00"]]}]}""",
            """{"kind":"weekly-availability","weekly":[{"days":["Monday"],"windows":[["09:00","17:00","18:00"]]}]}""",
            """{"kind":"weekly-availability","weekly":[{"days":["Monday"],"windows":[["09:00","25:00"]]}]}""",
            // Hour 24 is a window's end, written 24:00, and stands in no other time and in no instant, whatever the
            // runtime's own parsers take.
            """{"kind":"weekly-availability","weekly":[{"days":["Monday"],"windows":[["24:00","09:00"]]}]}""",
            """{"kind":"weekly-availability","weekly":[{"days":["Monday"],"windows":[["09:00","24:00:00"]]}]}""",
            """{"kind":"daily-local-time","times":["24:00"]}""",
            """{"kind":"explicit-instants","instants":["2027-01-05T24:00:00Z"]}""",
            """{"kind":"explicit-instants","instants":["2027-01-05T24:00:00+01:00"]}""",
            """{"kind":"explicit-instants","instants":["2027-01-05T24:00"]}""",
            """{"kind":"weekly-availability","weekly":[{"days":["Monday"],"windows":[["09:00","17:00"]]}],"excludedRanges":[{"fromUtc":"2027-01-05T24:00:00Z","toUtc":"2027-01-07T00:00:00Z"}]}""",
            """{"kind":"explicit-instants","instants":["2027-01-05T10:60:00Z"]}""",
            """{"kind":"weekly-availability","weekly":[{"days":["Monday"],"windows":[["09:00","17:00"]],"note":"x"}]}""",
            """{"kind":"weekly-availability","weekly":[{"days":["Monday"],"windows":[["09:00","17:00"]]}],"excludedRanges":[{"from":"2027-08-01"}]}""",
            """{"kind":"weekly-availability","weekly":[{"days":["Monday"],"windows":[["09:00","17:00"]]}],"excludedRanges":[{"from":"2027-08-01","to":"2027-08-15","toUtc":"2027-01-05T11:00:00Z"}]}""",
            """{"kind":"weekly-availability","weekly":[{"days":["Monday"],"windows":[["09:00","17:00"]]}],"excludedRanges":[{"fromUtc":"2027-01-05T10:00:00","toUtc":"2027-01-05T11:00:00Z"}]}""",
        ];

    /// <summary>Well-formed definitions the compiler refuses for a rule no schema of shapes can state.</summary>
    public static TheoryData<string> BeyondShape() =>
        [
            """{"kind":"weekly-availability","weekly":[{"days":["Monday"],"windows":[["09:00","09:00"]]}]}""",
            """{"kind":"weekly-availability","weekly":[{"days":["Monday"],"windows":[["09:00","17:00"]]}],"excludedRanges":[{"from":"2027-08-15","to":"2027-08-01"}]}""",
            """{"kind":"weekly-availability","weekly":[{"days":["Monday"],"windows":[["09:00","17:00"]]}],"excludedRanges":[{"fromUtc":"2027-01-05T11:00:00Z","toUtc":"2027-01-05T10:00:00Z"}]}""",
            """{"kind":"daily-local-time","times":["09:00"],"excludedDates":["2027-02-30"]}""",
            """{"kind":"daily-local-time","times":["09:00"],"timeZone":"Mars/Olympus"}""",
        ];

    [Theory]
    [MemberData(nameof(Accepted))]
    public async Task A_definition_the_compiler_takes_passes_the_published_schema_and_the_draft_request(string json)
    {
        Assert.True(Compiles(json), "The compiler refuses this definition in both roles; it is no example of an accepted one.");
        Assert.True(PassesSchema(json), "The compiler takes this definition and the published schema refuses it.");
        Assert.True(await PassesDraftRequestAsync(json), "The compiler takes this definition and the draft request's schema refuses it.");
    }

    /// <summary>
    /// A draft takes a definition as a file holds it: every member of a definition and, at its root alone, the $schema the
    /// file names. A read answers the stored definition, which holds none, so the read's schema and an operand's take none.
    /// </summary>
    [Fact]
    public async Task Only_the_root_of_a_draft_definition_takes_a_schema_member()
    {
        var schemas = await Schemas.Value;
        var stored = schemas[ActaOpenApiDocument.CalendarDefinitionSchema]!.AsObject();
        var drafted = schemas[ActaOpenApiDocument.CalendarDefinitionDocumentSchema]!.AsObject();
        string Reference(string owner) => schemas[owner]!["properties"]!["definition"]!["$ref"]!.GetValue<string>();

        Assert.Equal("#/components/schemas/" + ActaOpenApiDocument.CalendarDefinitionDocumentSchema, Reference("SaveCalendarDraftRequest"));
        Assert.Equal("#/components/schemas/" + ActaOpenApiDocument.CalendarDefinitionSchema, Reference("GetCalendarResponse"));

        // Apart from its description and the one member, the drafted definition is the stored one, member for member.
        var expected = stored.DeepClone().AsObject();
        expected["description"] = drafted["description"]!.DeepClone();
        var members = new JsonObject { ["$schema"] = drafted["properties"]!["$schema"]!.DeepClone() };
        foreach (var (name, member) in stored["properties"]!.AsObject())
        {
            members[name] = member!.DeepClone();
        }
        expected["properties"] = members;
        Assert.True(JsonNode.DeepEquals(expected, drafted), drafted.ToJsonString());
        Assert.Equal("string", drafted["properties"]!["$schema"]!["type"]!.GetValue<string>());
        Assert.NotEqual(stored["description"]!.GetValue<string>(), drafted["description"]!.GetValue<string>());

        Assert.True(
            await PassesDraftRequestAsync($$"""{"$schema":"https://useacta.net/schemas/calendar-definition-v1.json",{{Daily[1..]}}""")
        );
        Assert.False(await PassesDraftRequestAsync($$"""{"$schema":5,{{Daily[1..]}}"""));
        Assert.False(await PassesDraftRequestAsync($$"""{"kind":"union","of":[{"$schema":"x",{{Daily[1..]}}]}"""));
        Assert.False(await PassesDraftRequestAsync($$"""{"kind":"at-opening","availability":{"$schema":"x",{{Weekly.Trim()[1..]}}}"""));
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void A_definition_the_published_schema_refuses_does_not_compile(string json)
    {
        Assert.False(PassesSchema(json), "The published schema takes this definition.");
        Assert.False(Compiles(json), "The published schema refuses a definition the compiler takes.");
    }

    [Theory]
    [MemberData(nameof(BeyondShape))]
    public void A_rule_beyond_shape_is_the_compilers_alone(string json)
    {
        Assert.True(PassesSchema(json), "The schema refuses this; move it to the refused definitions.");
        Assert.False(Compiles(json), "The compiler takes this; it is an accepted definition.");
    }

    // The site serves the file its $id names, so the two copies are one text.
    [Fact]
    public void The_site_serves_the_schema_the_reference_holds()
    {
        var reference = File.ReadAllText(SchemaFile("docs", "reference")).ReplaceLineEndings("\n");
        var served = File.ReadAllText(Path.Combine(DocSampleExtraction.RepoRoot, "site", "schemas", "calendar-definition-v1.json"))
            .ReplaceLineEndings("\n");

        Assert.Equal(reference, served);
        using var schema = JsonDocument.Parse(reference);
        Assert.Equal("https://useacta.net/schemas/calendar-definition-v1.json", schema.RootElement.GetProperty("$id").GetString());
    }

    private static string SchemaFile(params string[] folder) =>
        Path.Combine([DocSampleExtraction.RepoRoot, .. folder, "calendar-definition.schema.json"]);

    private static bool PassesSchema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Published.Value.Evaluate(document.RootElement).IsValid;
    }

    private static async Task<bool> PassesDraftRequestAsync(string definition)
    {
        using var body = JsonDocument.Parse($$"""{"role":"occurrence","definition":{{definition}}}""");
        return (await DraftRequest.Value).Evaluate(body.RootElement).IsValid;
    }

    private static bool Compiles(string json)
    {
        foreach (var role in new[] { CalendarRoleCode.Occurrence, CalendarRoleCode.Availability })
        {
            try
            {
                CalendarDefinition.Compile(role, json);
                return true;
            }
            catch (CalendarDefinitionException) { }
        }

        return false;
    }
}
