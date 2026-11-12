using System.Globalization;
using Microsoft.OpenApi;

namespace Acta.Tests.Contracts;

// The calendar definition language. The engine reads a definition as JSON, not through a record, so the generator sees
// no type to describe: this schema is written by hand, and CalendarDefinitionSchemaTests holds it to what the compiler
// accepts.
internal static partial class ActaOpenApiDocument
{
    /// <summary>The schema of a calendar definition: one object that takes every kind's members.</summary>
    public const string CalendarDefinitionSchema = "CalendarDefinition";

    /// <summary>The schema of the definition a draft takes: a calendar definition whose root may name its JSON Schema.</summary>
    public const string CalendarDefinitionDocumentSchema = "CalendarDefinitionDocument";

    private const string WeeklyRuleSchema = "CalendarWeeklyRule";
    private const string LocalWindowSchema = "CalendarLocalWindow";
    private const string ExcludedRangeSchema = "CalendarExcludedRange";

    private const string Zone =
        "a time zone id the host knows, such as Europe/Ljubljana, or schedule for the zone of the schedule that uses the calendar, "
        + "which is also what leaving it out means.";

    // Adds the definition's schemas and points the draft's and the read's definition at them; each keeps its own text.
    // The draft takes the definition as a file holds it, and the read answers the stored one, which names no schema.
    private static void AddCalendarDefinition(IDictionary<string, IOpenApiSchema> schemas)
    {
        var used = false;
        foreach (
            var (name, definition) in new[]
            {
                ("SaveCalendarDraftRequest", CalendarDefinitionDocumentSchema),
                ("GetCalendarResponse", CalendarDefinitionSchema),
            }
        )
        {
            if (schemas.TryGetValue(name, out var owner) && owner is OpenApiSchema { Properties: { } properties })
            {
                properties["definition"] = new OpenApiSchemaReference(definition);
                used = true;
            }
        }

        if (!used)
        {
            return;
        }

        // One object with every kind's members rather than a oneOf of one schema per kind: generated clients fail on a
        // per-kind oneOf whose kind is a plain string, and holding kind to its value breaks the generated C# client.
        schemas[CalendarDefinitionSchema] = Object(
            "A calendar's definition: a JSON object whose kind names its shape. An availability family takes weekly-availability, "
                + "union, intersection, or subtraction; an occurrence family takes explicit-instants, daily-local-time, business-day, "
                + "at-opening, or union. Each member names the kinds that take it, and the server refuses it on any other kind. At "
                + "most 64 KB, 8 levels of JSON, and 5000 list items.",
            ["kind"],
            DefinitionMembers()
        );
        // The same members again rather than an allOf over the definition: the definition refuses a member it does not
        // declare, so a schema composed from it could never add one.
        schemas[CalendarDefinitionDocumentSchema] = Object(
            "A calendar's definition as a draft takes it: every member of a calendar definition, read as there, and at the root "
                + "alone the $schema a definition kept in a file may name. A definition written inline as an operand takes no "
                + "$schema, and a read answers the definition without it.",
            ["kind"],
            [
                (
                    "$schema",
                    Text(
                        "The JSON Schema the definition is written against, https://useacta.net/schemas/calendar-definition-v1.json, "
                            + "which an editor reads to check and complete a file. The server reads it and does not store it, so it "
                            + "changes no content hash and makes no version."
                    )
                ),
                .. DefinitionMembers(),
            ]
        );
        schemas[WeeklyRuleSchema] = Object(
            "The windows that are open on some weekdays.",
            ["days", "windows"],
            ("days", Weekdays("The weekdays the windows apply on, each Monday to Sunday; case is ignored.")),
            ("windows", List("The windows open on those weekdays.", Ref(LocalWindowSchema), 1))
        );
        schemas[LocalWindowSchema] = new OpenApiSchema
        {
            Type = JsonSchemaType.Array,
            MinItems = 2,
            MaxItems = 2,
            Description =
                "A window as a pair of local times, its start then its end, each HH:mm or HH:mm:ss, such as 09:00 and 17:00. The start is open time and the end is not. An end "
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
            ("toUtc", Text("The instant the exclusion ends, in UTC with a Z, after fromUtc; the calendar is open again from that instant."))
        );
    }

    // The members the kinds take between them, built anew for each schema that holds them.
    private static (string Name, IOpenApiSchema Schema)[] DefinitionMembers()
    {
        var note = Text("A note for operators, which every kind takes; it changes nothing the calendar computes.");
        note.MaxLength = 512;
        return
        [
            (
                "kind",
                Text(
                    "The definition's kind: weekly-availability, union, intersection, subtraction, explicit-instants, daily-local-time, "
                        + "business-day, or at-opening. A union is open while any operand is, or fires whenever one does, and an "
                        + "intersection is open only while every operand is; the other kinds' members say what each computes. A later "
                        + "version of Acta may add kinds."
                )
            ),
            ("description", note),
            (
                "timeZone",
                Text(
                    "The zone a weekly-availability, explicit-instants, daily-local-time, or business-day calendar reads its local "
                        + "dates and times in: "
                        + Zone
                )
            ),
            (
                "weekly",
                List(
                    "The weekly rules of a weekly-availability calendar, which needs at least one weekly rule or date override; the "
                        + "windows of every rule that names a weekday apply on it.",
                    Ref(WeeklyRuleSchema)
                )
            ),
            (
                "dateOverrides",
                new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Description =
                        "The dates of a weekly-availability calendar whose windows replace the weekly ones, keyed by the date as "
                        + "yyyy-MM-dd; an empty list closes the date.",
                    AdditionalProperties = List("The date's windows.", Ref(LocalWindowSchema)),
                }
            ),
            (
                "excludedRanges",
                List(
                    "The ranges a weekly-availability calendar cuts out of its windows after everything else is applied.",
                    Ref(ExcludedRangeSchema)
                )
            ),
            (
                "of",
                List(
                    "The operands of a union or an intersection: at least one definition written inline, each of the family's role "
                        + "in a union and each an availability definition in an intersection. Required.",
                    Ref(CalendarDefinitionSchema),
                    minItems: 1
                )
            ),
            (
                "from",
                Ref(
                    CalendarDefinitionSchema,
                    "The calendar a subtraction requires to be open: an availability definition, written inline. Required."
                )
            ),
            (
                "minus",
                Ref(
                    CalendarDefinitionSchema,
                    "The calendar a subtraction requires not to be open: an availability definition, written inline. Required."
                )
            ),
            (
                "instants",
                List(
                    "The instants an explicit-instants calendar fires at. Required.",
                    Text(
                        "An instant with its offset, such as 2027-01-15T16:00:00Z, or a local date-time in the calendar's zone, such as "
                            + "2027-01-15T16:00."
                    ),
                    1
                )
            ),
            ("times", List("The local times a daily-local-time calendar fires at. Required.", Text("A local time, HH:mm or HH:mm:ss."), 1)),
            (
                "days",
                Weekdays(
                    "The weekdays a daily-local-time calendar fires on, each Monday to Sunday, case ignored; every weekday when left out."
                )
            ),
            ("excludedDates", List("The dates a daily-local-time calendar skips.", Text("A date, yyyy-MM-dd."))),
            ("select", Text("Which end of the period a business-day calendar counts from: first or last. Required.")),
            ("period", Text("The period a business-day calendar counts in: week, month, quarter, or year. Required.")),
            (
                "nth",
                new OpenApiSchema
                {
                    Type = JsonSchemaType.Integer,
                    Format = "int32",
                    Minimum = 1.ToString(CultureInfo.InvariantCulture),
                    Maximum = 31.ToString(CultureInfo.InvariantCulture),
                    Description =
                        "Which business day a business-day calendar fires on, counted from the end of the period that select names, 1 to "
                        + "31; 1 when left out.",
                }
            ),
            (
                "businessDays",
                Weekdays("The weekdays a business-day calendar counts as business days, case ignored; Monday to Friday when left out.")
            ),
            ("holidays", List("The dates a business-day calendar counts as no business days.", Text("A date, yyyy-MM-dd."))),
            ("time", Text("The local time a business-day calendar fires at, HH:mm or HH:mm:ss. Required.")),
            (
                "availability",
                Ref(
                    CalendarDefinitionSchema,
                    "The availability calendar whose openings an at-opening calendar fires at, written inline. Required."
                )
            ),
        ];
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
