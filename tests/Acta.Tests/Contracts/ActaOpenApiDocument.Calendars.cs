using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace Acta.Tests.Contracts;

// The calendar definition language. The engine reads a definition as JSON, not through a record, so the generator sees
// no type to describe: these schemas are written by hand, one per kind, and CalendarDefinitionSchemaTests holds them to
// what the compiler accepts.
internal static partial class ActaOpenApiDocument
{
    /// <summary>The schema of a calendar definition: one of its kinds' schemas, chosen by kind.</summary>
    public const string CalendarDefinitionSchema = "CalendarDefinition";

    /// <summary>Each kind's schema name; <c>kind</c> is a plain string in each, since a one-value enum breaks a generated C# union.</summary>
    public static readonly IReadOnlyDictionary<string, string> CalendarKindSchemas = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["weekly-availability"] = "WeeklyAvailabilityCalendar",
        ["union"] = "UnionCalendar",
        ["intersection"] = "IntersectionCalendar",
        ["subtraction"] = "SubtractionCalendar",
        ["explicit-instants"] = "ExplicitInstantsCalendar",
        ["daily-local-time"] = "DailyLocalTimeCalendar",
        ["business-day"] = "BusinessDayCalendar",
        ["at-opening"] = "AtOpeningCalendar",
    };

    private const string WeeklyRuleSchema = "CalendarWeeklyRule";
    private const string LocalWindowSchema = "CalendarLocalWindow";
    private const string ExcludedRangeSchema = "CalendarExcludedRange";

    private const string Zone =
        "a time zone id the host knows, such as Europe/Ljubljana, or schedule for the zone of the schedule that uses the calendar, "
        + "which is also what leaving it out means.";

    // Adds the definition's schemas and points the draft's and the read's definition at them; each keeps its own text.
    private static void AddCalendarDefinition(IDictionary<string, IOpenApiSchema> schemas)
    {
        var used = false;
        foreach (var name in new[] { "SaveCalendarDraftRequest", "GetCalendarResponse" })
        {
            if (schemas.TryGetValue(name, out var owner) && owner is OpenApiSchema { Properties: { } properties })
            {
                properties["definition"] = new OpenApiSchemaReference(CalendarDefinitionSchema);
                used = true;
            }
        }

        if (!used)
        {
            return;
        }

        schemas[CalendarDefinitionSchema] = new OpenApiSchema
        {
            Description =
                "A calendar's definition: a JSON object whose kind names its shape. An availability family takes weekly-availability, "
                + "union, intersection, or subtraction; an occurrence family takes explicit-instants, daily-local-time, business-day, "
                + "at-opening, or union. A definition holds at most 64 KB, 8 levels of JSON, and 5000 list items in all. A later "
                + "version of Acta may add kinds.",
            OneOf = [.. CalendarKindSchemas.Values.Select(IOpenApiSchema (name) => new OpenApiSchemaReference(name))],
            Discriminator = new OpenApiDiscriminator
            {
                PropertyName = "kind",
                Mapping = CalendarKindSchemas.ToDictionary(
                    kind => kind.Key,
                    kind => new OpenApiSchemaReference(kind.Value),
                    StringComparer.Ordinal
                ),
            },
        };

        schemas["WeeklyAvailabilityCalendar"] = Kind(
            "weekly-availability",
            "An availability calendar of weekly windows in local time, with single dates overridden and ranges cut out. It needs at "
                + "least one weekly rule or date override.",
            [],
            ("timeZone", Text("The zone the local times are in: " + Zone)),
            ("weekly", List("The weekly rules; the windows of every rule that names a weekday apply on it.", Ref(WeeklyRuleSchema))),
            (
                "dateOverrides",
                new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Description =
                        "Dates whose windows replace the weekly ones, keyed by the date as yyyy-MM-dd; an empty list closes the date.",
                    AdditionalProperties = List("The date's windows.", Ref(LocalWindowSchema)),
                }
            ),
            ("excludedRanges", List("Ranges cut out of the windows after everything else is applied.", Ref(ExcludedRangeSchema)))
        );
        schemas[WeeklyRuleSchema] = Object(
            "The windows that are open on some weekdays.",
            ["days", "windows"],
            ("days", Weekdays("The weekdays the windows apply on, each Monday to Sunday; case is ignored.")),
            ("windows", List("The windows open on those weekdays.", Ref(LocalWindowSchema)))
        );
        schemas[LocalWindowSchema] = new OpenApiSchema
        {
            Type = JsonSchemaType.Array,
            MinItems = 2,
            MaxItems = 2,
            Description =
                "A window as a pair of local times, its start then its end, each HH:mm or HH:mm:ss, such as 09:00 and 17:00. An end "
                + "before the start is on the next date, and an end of 24:00 is the end of the day.",
            Items = Text("A local time, HH:mm or HH:mm:ss; 24:00 as a window's end."),
        };
        schemas[ExcludedRangeSchema] = Object(
            "A range cut out of an availability calendar: either whole dates, from and to, or instants, fromUtc and toUtc. A range "
                + "names one pair, never both.",
            [],
            ("from", Text("The first excluded date, yyyy-MM-dd, in the calendar's zone.")),
            ("to", Text("The last excluded date, yyyy-MM-dd; it is excluded too, and is not before from.")),
            ("fromUtc", Text("The instant the exclusion starts, in UTC with a Z, such as 2027-01-15T16:00:00Z.")),
            ("toUtc", Text("The instant the exclusion ends, in UTC with a Z, after fromUtc."))
        );
        schemas["UnionCalendar"] = Kind(
            "union",
            "The union of calendars of the family's role: an availability union is open whenever one of its operands is, and an "
                + "occurrence union fires at every instant one of its operands does.",
            ["of"],
            ("of", List("The operands, each a definition of the family's role, written inline.", Ref(CalendarDefinitionSchema), 1))
        );
        schemas["IntersectionCalendar"] = Kind(
            "intersection",
            "An availability calendar that is open only while every operand is.",
            ["of"],
            ("of", List("The operands, each an availability definition, written inline.", Ref(CalendarDefinitionSchema), 1))
        );
        schemas["SubtractionCalendar"] = Kind(
            "subtraction",
            "An availability calendar that is open while one calendar is and another is not.",
            ["from", "minus"],
            ("from", Ref(CalendarDefinitionSchema, "The calendar that must be open: an availability definition, written inline.")),
            ("minus", Ref(CalendarDefinitionSchema, "The calendar that must not be open: an availability definition, written inline."))
        );
        schemas["ExplicitInstantsCalendar"] = Kind(
            "explicit-instants",
            "An occurrence calendar that fires at the instants it lists.",
            ["instants"],
            ("timeZone", Text("The zone the local date-times are in: " + Zone)),
            (
                "instants",
                List(
                    "The instants to fire at.",
                    Text(
                        "An instant with its offset, such as 2027-01-15T16:00:00Z, or a local date-time in the calendar's zone, such as "
                            + "2027-01-15T16:00."
                    ),
                    1
                )
            )
        );
        schemas["DailyLocalTimeCalendar"] = Kind(
            "daily-local-time",
            "An occurrence calendar that fires at local times on chosen weekdays, leaving out listed dates.",
            ["times"],
            ("timeZone", Text("The zone the local times are in: " + Zone)),
            ("times", List("The local times to fire at.", Text("A local time, HH:mm or HH:mm:ss."), 1)),
            ("days", Weekdays("The weekdays to fire on, each Monday to Sunday, case ignored; every weekday when left out.")),
            ("excludedDates", List("Dates to skip.", Text("A date, yyyy-MM-dd.")))
        );
        schemas["BusinessDayCalendar"] = Kind(
            "business-day",
            "An occurrence calendar that fires at a local time on the nth business day from the start or the end of each week, month, "
                + "quarter, or year.",
            ["select", "of", "time"],
            ("timeZone", Text("The zone the dates and the time are in: " + Zone)),
            ("select", Text("Which end of the period to count from: first or last.")),
            ("of", Text("The period: week, month, quarter, or year.")),
            (
                "nth",
                new OpenApiSchema
                {
                    Type = JsonSchemaType.Integer,
                    Format = "int32",
                    Minimum = 1.ToString(CultureInfo.InvariantCulture),
                    Maximum = 31.ToString(CultureInfo.InvariantCulture),
                    Default = JsonValue.Create(1),
                    Description = "Which business day counted from that end, 1 to 31; 1 when left out.",
                }
            ),
            ("businessDays", Weekdays("The weekdays that are business days, case ignored; Monday to Friday when left out.")),
            ("holidays", List("Dates that are no business days.", Text("A date, yyyy-MM-dd."))),
            ("time", Text("The local time to fire at, HH:mm or HH:mm:ss."))
        );
        schemas["AtOpeningCalendar"] = Kind(
            "at-opening",
            "An occurrence calendar that fires each time an availability calendar opens.",
            ["availability"],
            ("availability", Ref(CalendarDefinitionSchema, "The availability calendar whose openings fire, written inline."))
        );
    }

    // One kind's schema: its kind and note, then its own members; the server refuses any other member.
    private static OpenApiSchema Kind(
        string kind,
        string description,
        string[] required,
        params (string Name, IOpenApiSchema Schema)[] members
    )
    {
        var note = Text("A note for operators; it changes nothing the calendar computes.");
        note.MaxLength = 512;
        return Object(
            description,
            ["kind", .. required],
            [("kind", Text($"The definition's kind: {kind}.")), ("description", note), .. members]
        );
    }

    private static OpenApiSchema Object(string description, string[] required, params (string Name, IOpenApiSchema Schema)[] members) =>
        new()
        {
            Type = JsonSchemaType.Object,
            Description = description,
            Required = required.ToHashSet(StringComparer.Ordinal),
            AdditionalPropertiesAllowed = false,
            Properties = members.ToDictionary(member => member.Name, member => member.Schema, StringComparer.Ordinal),
        };

    private static OpenApiSchema Text(string description) => new() { Type = JsonSchemaType.String, Description = description };

    private static OpenApiSchema List(string description, IOpenApiSchema items, int? minItems = null) =>
        new()
        {
            Type = JsonSchemaType.Array,
            Description = description,
            MinItems = minItems,
            Items = items,
        };

    private static OpenApiSchema Weekdays(string description) => List(description, Text("A weekday, Monday to Sunday."), 1);

    private static OpenApiSchemaReference Ref(string schema, string? description = null) => new(schema) { Description = description };
}
