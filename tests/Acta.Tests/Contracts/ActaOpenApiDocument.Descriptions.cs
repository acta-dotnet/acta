using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Acta.AspNetCore;
using Microsoft.OpenApi;

namespace Acta.Tests.Contracts;

internal static partial class ActaOpenApiDocument
{
    /// <summary>One sentence per tag: what its operations act on.</summary>
    private static readonly Dictionary<string, string> TagDescriptions = new(StringComparer.Ordinal)
    {
        ["Jobs"] =
            "List and enqueue jobs, read one job with its steps, checkpoints, events, input, and result, and control, signal, or tag it.",
        ["Namespaces"] = "List and read namespaces, and suspend, resume, update, or tag one.",
        ["Tenants"] = "Register, list, and read tenants, and suspend, resume, update, or tag one.",
        ["Alerts"] = "List and read alerts, and acknowledge or resolve one.",
        ["Outbox"] = "List the outbox relay's sources and a source's quarantined staging rows, and requeue or discard those rows.",
        ["Definitions"] =
            "List and read job definitions, their input templates and events, and update one's policy overrides, retire it, or tag it.",
        ["Schedules"] =
            "List and read schedules, preview one's upcoming fire instants, and pause, resume, or trigger it; a schedule's events are "
            + "read on its slot job or its definition.",
        ["Calendars"] =
            "List and read calendar families, their versions and the schedules naming them, preview a version, and save, publish, or archive one.",
        ["Workers"] = "List and read the workers that run jobs.",
        ["Events"] = "List the ledger's events, filtered and paged by cursor.",
        ["Overview"] = "Read the ledger's health counters in one call.",
        ["Capabilities"] = "Read what this host's API allows, such as whether controls are mapped.",
    };

    // What a status means on a read (a GET) and on a control (any other method), written once per status and route
    // shape: a read's 200 tells a page, a payload, and one resource apart, and a control's 404 and 409 tell its response
    // from a problem. A status none of these covers keeps the framework's default, which the description rule refuses.
    private static string? ResponseDescription(
        string status,
        bool read,
        OpenApiResponse response,
        IDictionary<string, IOpenApiSchema> schemas
    )
    {
        var content = response.Content ?? new Dictionary<string, OpenApiMediaType>();
        var problem = content.ContainsKey("application/problem+json");
        return (status, read, problem) switch
        {
            ("200", true, _) when IsPage(content, schemas) =>
                "One page of the list; while hasMore is true, pass its nextCursor back as cursor for the next page.",
            ("200", true, _) when content.ContainsKey(RawPayload.BinaryContentType) =>
                "The stored payload itself: a JSON payload as application/json, any other format as application/octet-stream.",
            ("200", true, _) => "The resource the route reads.",
            ("200", false, _) => "The call ran; the body says what it did.",
            ("201", _, _) => "The resource was created, and Location holds its URL.",
            ("202", _, _) => "The command is stored for the next relay pass to apply, so nothing has changed yet.",
            ("204", _, _) => "Nothing is stored to return.",
            ("400", true, _) => "A path or query parameter is malformed or missing; the problem's detail names it.",
            ("400", false, _) =>
                "The call is not confirmed, a path parameter cannot name a target, or the body breaks a rule; the problem's detail "
                    + "says which.",
            ("401", _, _) => "The call carries no credential the host accepts.",
            ("403", _, _) =>
                "The call was refused before it ran: by the local-only guard, or because the caller lacks the operation's permission.",
            ("404", false, false) => "The target does not exist; the body's action is not-found and nothing was written.",
            ("404", _, _) => "The target the path names, or one a filter names, does not exist.",
            ("409", true, _) => "This host cannot serve the read as asked; the problem's detail says why.",
            ("409", false, true) => "The call was refused as a whole; the problem's reasonCode says why.",
            ("409", false, false) =>
                "The control was refused and nothing was written; the body's action says why: rejected by the target's state, or "
                    + "version-conflict where the call carries an expected version.",
            ("413", _, _) => "The body is larger than the server accepts.",
            ("415", _, _) => "The body is not application/json.",
            ("500", _, _) => "The server failed unexpectedly; the problem document leaves the cause to the host's log.",
            ("503", _, _) => "The database could not be reached or refused the call; retry later.",
            _ => null,
        };
    }

    // A status whose cause on one route is narrower than its shape's text says that cause instead, keyed by operation id.
    private static readonly Dictionary<(string Operation, string Status), string> RouteResponses = new()
    {
        [("previewSchedule", "409")] =
            "A calendar the schedule names resolves to nothing on this host: no calendar of that name and role is declared here, and "
            + "neither the namespace's catalog nor the shared one has a current version of one. The problem's detail names the calendar.",
        [("listQuarantinedOutbox", "409")] =
            "This host registers no outbox relay for the namespace, and only one that does can read its source database; list the "
            + "quarantine on a host whose source reports isLocal.",
        [("archiveCalendar", "409")] =
            "The archive was refused and nothing was written: the body's action is rejected, while a schedule still names the "
            + "family or when its current version is not the expected one.",
    };

    // A page is a response whose schema carries a next cursor.
    private static bool IsPage(IDictionary<string, OpenApiMediaType> content, IDictionary<string, IOpenApiSchema> schemas) =>
        content.TryGetValue("application/json", out var media)
        && media.Schema is OpenApiSchemaReference { Reference.Id: { } id }
        && schemas.TryGetValue(id, out var target)
        && target.Properties?.ContainsKey("nextCursor") == true;

    /// <summary>
    /// The XML documentation of the Acta types behind the document's schemas, in wire terms: a type's summary describes
    /// its schema and a member's (a record's param line) its property. A cref to a member on the wire reads as its wire
    /// name, to a schema's type as the schema's name, to a constant as its value, to any other public symbol as plain
    /// words, and to an internal one as the cref itself, which the description rule refuses.
    /// </summary>
    private sealed partial class SchemaDocs
    {
        private static readonly Assembly[] DocumentedAssemblies = [typeof(IJobs).Assembly, typeof(ActaApiJsonContext).Assembly];

        private static readonly Lazy<Dictionary<string, XElement>> Members = new(() =>
            DocumentedAssemblies
                .SelectMany(assembly => XDocument.Load(Path.ChangeExtension(assembly.Location, ".xml")).Descendants("member"))
                .ToDictionary(member => member.Attribute("name")!.Value, StringComparer.Ordinal)
        );

        private const BindingFlags AnyMember = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        private readonly Dictionary<string, Type> types = new(StringComparer.Ordinal);
        private readonly Dictionary<Type, JsonTypeInfo> contracts = [];
        private IDictionary<string, IOpenApiSchema> schemas = new Dictionary<string, IOpenApiSchema>();

        [GeneratedRegex(@"\s+")]
        private static partial Regex Whitespace();

        [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
        private static partial Regex Humps();

        /// <summary>Notes the Acta type a schema is made from, and an object's serializer contract.</summary>
        public void Record(JsonTypeInfo info)
        {
            var type = Nullable.GetUnderlyingType(info.Type) ?? info.Type;
            if (!DocumentedAssemblies.Contains(type.Assembly))
            {
                return;
            }

            if (types.TryGetValue(type.Name, out var known) && known != type)
            {
                throw new InvalidOperationException($"{known} and {type} would share the schema name {type.Name}.");
            }
            types[type.Name] = type;
            if (info.Kind == JsonTypeInfoKind.Object && info.Type == type)
            {
                contracts[type] = info;
            }
        }

        /// <summary>
        /// Gives each schema and property its type's or member's summary. A description the document's own code wrote
        /// stands, and a type or member with no summary keeps what it has.
        /// </summary>
        public void Describe(IDictionary<string, IOpenApiSchema> components)
        {
            schemas = components;
            foreach (var (name, entry) in components)
            {
                if (entry is not OpenApiSchema schema || TypeOf(name) is not { } type)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(schema.Description))
                {
                    schema.Description = Summary("T:" + DocId(type), type);
                }

                if (schema.Properties is not { } properties || !contracts.TryGetValue(type, out var contract))
                {
                    continue;
                }

                foreach (var property in contract.Properties)
                {
                    if (
                        !properties.TryGetValue(property.Name, out var member)
                        || property.AttributeProvider is not MemberInfo source
                        || Summary(MemberId(source), source.DeclaringType!) is not { } text
                    )
                    {
                        continue;
                    }

                    properties[property.Name] = member switch
                    {
                        OpenApiSchemaReference { Reference.Description: null or "" } reference => new OpenApiSchemaReference(
                            reference.Reference.Id!
                        )
                        {
                            Description = text,
                        },
                        OpenApiSchema { Description: null or "" } inline => Described(inline, text),
                        _ => member,
                    };
                }
            }
        }

        private static OpenApiSchema Described(OpenApiSchema schema, string text)
        {
            schema.Description = text;
            return schema;
        }

        // A schema's type: the one the generator saw under that name, else the one Acta type of that name, as for the
        // codes the document adds itself.
        private Type? TypeOf(string schemaName) =>
            types.GetValueOrDefault(schemaName)
            ?? (
                DocumentedAssemblies.SelectMany(assembly => assembly.GetTypes()).Where(type => type.Name == schemaName).ToList()
                    is [var only]
                    ? only
                    : null
            );

        private string? SchemaName(Type type) => schemas.ContainsKey(type.Name) && TypeOf(type.Name) == type ? type.Name : null;

        private string? Summary(string id, Type owner) =>
            Members.Value.GetValueOrDefault(id)?.Element("summary") is { } summary
            && Whitespace().Replace(Render(summary, owner), " ").Trim() is { Length: > 0 } text
                ? text
                : null;

        private string Render(XElement element, Type owner) => string.Concat(element.Nodes().Select(node => Render(node, owner)));

        private string Render(XNode node, Type owner) =>
            node switch
            {
                XText text => text.Value,
                XElement { Name.LocalName: "see" or "seealso" } see when see.Attribute("cref") is { } cref => Cref(cref.Value),
                XElement { Name.LocalName: "see" } see when see.Attribute("langword") is { } word => word.Value,
                XElement { Name.LocalName: "paramref" } reference => ParameterReference(reference.Attribute("name")!.Value, owner),
                XElement { Name.LocalName: "typeparamref" } reference => reference.Attribute("name")!.Value,
                XElement { Name.LocalName: "para" or "item" or "br" } block => " " + Render(block, owner) + " ",
                XElement element => Render(element, owner),
                _ => "",
            };

        // A record's parameter is its property of the same name.
        private string ParameterReference(string name, Type owner) =>
            owner.GetProperty(name, AnyMember | BindingFlags.IgnoreCase) is { } property ? Words(property) : Words(name);

        private string Cref(string id)
        {
            var name = id[2..].Split('(')[0];
            if (id.StartsWith("T:", StringComparison.Ordinal))
            {
                return FindType(name) is { } type ? Words(type) : Refused(id);
            }

            var dot = name.LastIndexOf('.');
            if (dot < 0 || FindType(name[..dot]) is not { } declaring)
            {
                return Refused(id);
            }

            var memberName = name[(dot + 1)..].Split("``")[0];
            return memberName == "#ctor" ? Words(declaring)
                : declaring.GetMember(memberName, AnyMember).FirstOrDefault() is { } member ? Words(member)
                : Refused(id);
        }

        // A type is its schema's name on the wire, plain words when public, and refused otherwise.
        private string Words(Type type) =>
            SchemaName(type)
            ?? (
                type.IsVisible
                    ? Words(type.IsInterface && type.Name.Length > 1 && char.IsUpper(type.Name[1]) ? type.Name[1..] : type.Name)
                    : Refused("T:" + DocId(type))
            );

        // A member is its wire name or wire spelling, a constant its value, plain words when public, and refused otherwise.
        private string Words(MemberInfo member)
        {
            var declaring = member.DeclaringType!;
            if (member is FieldInfo { IsLiteral: true } field)
            {
                if (!declaring.IsEnum)
                {
                    return Convert.ToString(field.GetRawConstantValue(), CultureInfo.InvariantCulture)!;
                }
                if (
                    SchemaName(declaring) is { } code
                    && schemas[code] is OpenApiSchema { Extensions: { } extensions }
                    && extensions.TryGetValue(KnownValuesExtension, out var extension)
                    && extension is JsonNodeExtension { Node: JsonArray known }
                )
                {
                    return known[Array.IndexOf(Enum.GetNames(declaring), field.Name)]!.GetValue<string>();
                }
            }

            if (
                SchemaName(declaring) is { } schema
                && contracts.TryGetValue(declaring, out var contract)
                && contract.Properties.FirstOrDefault(property => (property.AttributeProvider as MemberInfo)?.Name == member.Name)
                    is { } wire
                && schemas[schema] is OpenApiSchema { Properties: { } properties }
                && properties.ContainsKey(wire.Name)
            )
            {
                return wire.Name;
            }

            return declaring.IsVisible && IsPublic(member) ? Words(member.Name) : Refused(MemberId(member));
        }

        private static bool IsPublic(MemberInfo member) =>
            member switch
            {
                PropertyInfo property => property.GetMethod?.IsPublic == true,
                FieldInfo field => field.IsPublic,
                MethodBase method => method.IsPublic,
                _ => false,
            };

        // Plain words: the name's humps lowercased, with no generic arity and no Async.
        private static string Words(string name)
        {
            var bare = name.Split('`')[0];
            bare = bare.EndsWith("Async", StringComparison.Ordinal) && bare.Length > 5 ? bare[..^5] : bare;
            return string.Join(' ', Humps().Split(bare)).ToLowerInvariant();
        }

        // What the description rule refuses: a symbol the wire cannot name, kept as written so the author finds it.
        private static string Refused(string id) => $"cref=\"{id}\"";

        private static Type? FindType(string name)
        {
            foreach (var assembly in DocumentedAssemblies)
            {
                // A nested type's id joins it to its parent with a dot where reflection writes a plus.
                for (var candidate = name; ; )
                {
                    if (assembly.GetType(candidate) is { } type)
                    {
                        return type;
                    }
                    var dot = candidate.LastIndexOf('.');
                    if (dot < 0)
                    {
                        break;
                    }
                    candidate = candidate[..dot] + "+" + candidate[(dot + 1)..];
                }
            }
            return null;
        }

        private static string DocId(Type type) => type.FullName!.Replace('+', '.');

        private static string MemberId(MemberInfo member) =>
            (
                member is FieldInfo ? "F:"
                : member is MethodBase ? "M:"
                : "P:"
            )
            + DocId(member.DeclaringType!)
            + "."
            + member.Name;
    }
}
