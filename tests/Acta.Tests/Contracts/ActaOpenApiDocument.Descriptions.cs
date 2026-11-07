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
    /// <summary>
    /// The XML documentation of the Acta types behind the document's schemas, in wire terms: a type's summary describes
    /// its schema and a member's (a record's param line) its property. A cref to a member on the wire reads as its wire
    /// name, to a schema's type as the schema's name, to a constant as its value, to any other public symbol as plain
    /// words, and to an internal one as the cref itself, which the description rule refuses.
    /// </summary>
    private sealed partial class SchemaDocs
    {
        private static readonly Assembly[] DocumentedAssemblies = [typeof(IJobs).Assembly, typeof(DashboardJsonContext).Assembly];

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
