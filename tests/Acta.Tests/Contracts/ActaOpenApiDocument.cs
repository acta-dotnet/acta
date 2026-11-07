using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Acta.AspNetCore;
using Acta.Tests.AspNetCore.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using Xunit;

namespace Acta.Tests.Contracts;

/// <summary>
/// Generates the HTTP contract from the real endpoint graph: one host over <see cref="StagedFakes"/>, every mount mapped,
/// one document per mount, its examples what that host answers. The transformers state what the serializer and the
/// handlers do that the generator cannot see, so a client generated from the document reads and sends what the server
/// writes and accepts. The document is produced here rather than at build time so the shipped <c>Acta.AspNetCore</c>
/// package carries no OpenAPI dependency.
/// </summary>
internal static partial class ActaOpenApiDocument
{
    /// <summary>The extension carrying an open code's known values: the wire spellings this version writes, in member order.</summary>
    public const string KnownValuesExtension = "x-acta-known-values";

    /// <summary>The sentence every code schema's description ends with.</summary>
    public const string OtherValuesMayAppear = "Other values may appear: keep a value this client does not know as it is.";

    /// <summary>The version of the HTTP contract under <c>/v1</c>, not the package version.</summary>
    private const string ContractVersion = "1.0.0";

    // Markers the schema transformer leaves for the document transformer, which alone knows a schema's direction.
    private const string OmittedMarker = "x-acta-omitted-when-null";
    private const string RequiredMarker = "x-acta-json-required";
    private const string EnumMarker = "x-acta-enum";

    private const string ControlParameter = "ActaControl";
    private const string ControlSchema = "ActaControlConfirmation";

    /// <summary>
    /// Request members a handler refuses when absent (a 400 naming the member), which the record cannot say without
    /// claiming a non-null its serializer does not enforce. <c>OpenApiContractTests</c> proves each row.
    /// </summary>
    public static readonly Dictionary<string, string[]> HandlerRequiredMembers = new(StringComparer.Ordinal)
    {
        ["EnqueueJobRequest"] = ["jobNamespace", "jobName"],
        ["UpdateJobInputRequest"] = ["input"],
        ["TagInput"] = ["name"],
        ["SaveCalendarDraftRequest"] = ["role", "definition", "expectedLatestVersion"],
        ["PublishCalendarRequest"] = ["candidateVersion", "expectedCurrentVersion"],
        ["ArchiveCalendarRequest"] = ["expectedCurrentVersion"],
        ["UpdateDefinitionOverridesRequest"] = ["expectedVersion", "overrides"],
        ["RetireDefinitionRequest"] = ["expectedVersion"],
        ["RegisterTenantRequest"] = ["tenantName"],
        ["UpdateNamespaceRequest"] = ["expectedVersion"],
        ["UpdateTenantRequest"] = ["expectedVersion"],
    };

    [GeneratedRegex(@"\{(?<name>[A-Za-z]+)[^}]*\}")]
    private static partial Regex RouteSegments();

    /// <summary>One Acta mount of the generating host: its pattern, whether it maps the controls, and whether those require the confirmation header.</summary>
    public sealed record Mount(string Pattern, bool Controls, bool RequireConfirmation = true)
    {
        public string DocumentName => Pattern.Trim('/');

        /// <summary>The mount's API root, below which every path reads <c>/v1/...</c>: <c>/acta/api</c> for <c>MapActa("/acta")</c>.</summary>
        public string ApiRoot => "/" + DocumentName + "/api";
    }

    /// <summary>The contract of the default mount, <c>/acta</c> with its controls.</summary>
    public static async Task<string> GenerateAsync() => (await GenerateAsync(new Mount("/acta", Controls: true)))["/acta"];

    /// <summary>One document per mount, each holding its own operations, keyed by the mount's pattern.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> GenerateAsync(params Mount[] mounts)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        // The generator reads the host's options; requests and responses go through DashboardJsonContext, whose numbers
        // are strict. The web default would document every integer as integer-or-string, which the server refuses.
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.NumberHandling = DashboardJsonContext.Default.Options.NumberHandling
        );
        var fakes = StagedFakes.Create();
        builder.Services.AddSingleton<IJobs>(fakes.Jobs);
        builder.Services.AddSingleton<IActaOperations>(fakes);
        // Filled once the host runs and before any document is read; the operations are the same under every mount.
        var examples = new Dictionary<string, OpenApiExamples.Example>(StringComparer.Ordinal);
        foreach (var mount in mounts)
        {
            builder.Services.AddOpenApi(mount.DocumentName, o => Configure(o, mount, examples));
        }

        var app = builder.Build();
        foreach (var mount in mounts)
        {
            // The dashboard UI is off: its index, asset, and SPA-fallback routes are not part of the API contract.
            var acta = app.MapActa(
                mount.Pattern,
                o =>
                {
                    o.Enabled = false;
                    o.EnableControls = mount.Controls;
                    o.RequireControlConfirmationHeader = mount.RequireConfirmation;
                }
            );
            // The handlers read their JSON body through ControlEndpointValidation rather than a bound parameter, so they
            // declare it as RequestBodyDoc metadata, translated into the framework's IAcceptsMetadata here only: in a
            // serving host the routing matcher would read it and answer the missing-body and content-type cases the
            // handlers answer themselves. Finally() so it runs after the per-endpoint conventions that write it.
            ((IEndpointConventionBuilder)acta).Finally(endpoint =>
            {
                if (endpoint.Metadata.OfType<RequestBodyDoc>().LastOrDefault() is { } doc)
                {
                    endpoint.Metadata.Add(new DeclaredRequestBody(doc));
                }
            });
        }
        app.MapOpenApi();
        await app.StartAsync(TestContext.Current.CancellationToken);
        await using var _ = app;
        foreach (
            var (key, example) in await OpenApiExamples.CaptureAsync(app, (mounts.FirstOrDefault(m => m.Controls) ?? mounts[0]).ApiRoot)
        )
        {
            examples[key] = example;
        }

        var documents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mount in mounts)
        {
            documents[mount.Pattern] = await app.GetTestClient()
                .GetStringAsync($"/openapi/{mount.DocumentName}.json", TestContext.Current.CancellationToken);
        }
        return documents;
    }

    /// <summary>How <c>DashboardJsonContext</c> writes one enum member, the only authority on it; a name it cannot write throws.</summary>
    public static string WireName(Type enumType, string memberName) =>
        JsonSerializer.Serialize(Enum.Parse(enumType, memberName), enumType, DashboardJsonContext.Default.Options).Trim('"');

    /// <summary>How the refused enqueue's problem writes its <c>reasonCode</c>, as ApiFilters spells it.</summary>
    public static string ReasonCode(EnqueueRejectionReason reason) => JsonNamingPolicy.KebabCaseLower.ConvertName(reason.ToString());

    /// <summary>An enum's known values in id order: the spelling the serializer writes for each member.</summary>
    public static JsonArray KnownValues(Type enumType) => [.. Enum.GetNames(enumType).Select(name => (JsonNode)WireName(enumType, name))];

    private static void Configure(OpenApiOptions o, Mount mount, Dictionary<string, OpenApiExamples.Example> examples)
    {
        // A document describes one mount: the operations under its pattern, never a second mount's.
        var prefix = mount.DocumentName + "/";
        o.ShouldInclude = description => (description.RelativePath ?? "").TrimStart('/').StartsWith(prefix, StringComparison.Ordinal);

        o.AddDocumentTransformer(
            (document, _, _) =>
            {
                document.Info.Title = "Acta API";
                document.Info.Version = ContractVersion;
                document.Info.Description = DocumentDescription;
                return Task.CompletedTask;
            }
        );
        // C# defaults on optional request members document nothing a reader needs and churn the file.
        o.AddSchemaTransformer(
            (schema, _, _) =>
            {
                schema.Default = null;
                return Task.CompletedTask;
            }
        );
        o.AddSchemaTransformer(ShapeSchema);
        var docs = new SchemaDocs();
        o.AddSchemaTransformer(
            (_, context, _) =>
            {
                docs.Record(context.JsonTypeInfo);
                return Task.CompletedTask;
            }
        );

        // A route table's handler reads its keys from the route values, unseen by the generator: each segment the
        // template names and no handler parameter binds is a required string path parameter, in template order. Every
        // path parameter, bound or not, says what its key names.
        o.AddOperationTransformer(
            (operation, context, _) =>
            {
                var names = RouteSegments().Matches(context.Description.RelativePath ?? "").Select(m => m.Groups["name"].Value).ToList();
                for (var i = 0; i < names.Count; i++)
                {
                    operation.Parameters ??= [];
                    if (!operation.Parameters.Any(p => p.In == ParameterLocation.Path && p.Name == names[i]))
                    {
                        operation.Parameters.Insert(
                            Math.Min(i, operation.Parameters.Count),
                            new OpenApiParameter
                            {
                                Name = names[i],
                                In = ParameterLocation.Path,
                                Required = true,
                                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                            }
                        );
                    }
                }
                foreach (
                    var parameter in (operation.Parameters ?? []).OfType<OpenApiParameter>().Where(p => p.In == ParameterLocation.Path)
                )
                {
                    parameter.Description ??= RouteKeys.Descriptions.GetValueOrDefault(parameter.Name!);
                }
                return Task.CompletedTask;
            }
        );
        // The handlers bind query strings through QueryBinding; each endpoint declares them as QueryParameterDoc. A code
        // filter references its family's schema, an open string whose known values come from the model.
        o.AddOperationTransformer(
            (operation, context, _) =>
            {
                foreach (var doc in context.Description.ActionDescriptor.EndpointMetadata.OfType<QueryParameterDoc>())
                {
                    IOpenApiSchema schema = doc.Kind switch
                    {
                        _ when doc.CodeKind is { } kind => new OpenApiSchemaReference(CodeFamily(kind).Name),
                        QueryParameterKind.Int => new OpenApiSchema
                        {
                            Type = JsonSchemaType.Integer,
                            Format = "int32",
                            Minimum = doc.Minimum?.ToString(CultureInfo.InvariantCulture),
                            Default = doc.Default is { } value ? JsonValue.Create(value) : null,
                        },
                        QueryParameterKind.Long => new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int64" },
                        QueryParameterKind.Bool => new OpenApiSchema { Type = JsonSchemaType.Boolean },
                        QueryParameterKind.Instant => new OpenApiSchema { Type = JsonSchemaType.String, Format = "date-time" },
                        _ => new OpenApiSchema { Type = JsonSchemaType.String },
                    };
                    operation.Parameters ??= [];
                    operation.Parameters.Add(
                        new OpenApiParameter
                        {
                            Name = doc.Name,
                            In = ParameterLocation.Query,
                            Required = doc.Required,
                            Description = doc.Description,
                            Schema = doc.Repeatable ? new OpenApiSchema { Type = JsonSchemaType.Array, Items = schema } : schema,
                        }
                    );
                }
                return Task.CompletedTask;
            }
        );
        // ASP.NET endpoint names stay unset, since a host may mount the API twice and names are global.
        o.AddOperationTransformer(
            (operation, context, _) =>
            {
                operation.OperationId = OpenApiOperationIds.For(
                    context.Description.HttpMethod ?? "",
                    context.Description.RelativePath ?? ""
                );
                return Task.CompletedTask;
            }
        );
        // A control carries the mount's confirmation header, one shared parameter whose one value is true.
        string? confirmationHeader = null;
        o.AddOperationTransformer(
            (operation, context, _) =>
            {
                if (Metadata<ControlEndpointDoc>(context) is { ConfirmationHeaderName: { } header })
                {
                    confirmationHeader = header;
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameterReference(ControlParameter));
                }
                return Task.CompletedTask;
            }
        );
        // A payload read answers the stored bytes themselves: any JSON value as JSON, any other format as raw bytes, which
        // OpenAPI 3.1 describes as a media type without a schema.
        o.AddOperationTransformer(
            (operation, context, _) =>
            {
                if (
                    context
                        .Description.ActionDescriptor.EndpointMetadata.OfType<IProducesResponseTypeMetadata>()
                        .Any(produces => produces.ContentTypes.Contains(RawPayload.BinaryContentType))
                    && operation.Responses?.TryGetValue("200", out var ok) == true
                    && ok is OpenApiResponse payload
                )
                {
                    payload.Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
                    {
                        [RawPayload.JsonContentType] = new() { Schema = new OpenApiSchema { Description = StoredJsonDescription } },
                        [RawPayload.BinaryContentType] = new(),
                    };
                }
                return Task.CompletedTask;
            }
        );
        // A response header an endpoint sets on its successes with a body (ResponseHeaderDoc).
        o.AddOperationTransformer(
            (operation, context, _) =>
            {
                if (Metadata<ResponseHeaderDoc>(context) is { } headerDoc)
                {
                    foreach (var (code, response) in operation.Responses ?? [])
                    {
                        if (code.StartsWith('2') && response is OpenApiResponse { Content.Count: > 0 } success)
                        {
                            success.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal);
                            success.Headers[headerDoc.Name] = new OpenApiHeader
                            {
                                Description = headerDoc.Description,
                                Schema = new OpenApiSchema
                                {
                                    Type = headerDoc.Name == RawPayload.FormatHeader ? JsonSchemaType.Integer : JsonSchemaType.String,
                                },
                            };
                        }
                    }
                }
                return Task.CompletedTask;
            }
        );
        // One example each for the JSON request body and the JSON success, as the host over the staged fakes was sent
        // and answered them (OpenApiExamples), named for the operation.
        o.AddOperationTransformer(
            (operation, context, _) =>
            {
                var key = OpenApiOperationIds.Key(context.Description.HttpMethod ?? "", context.Description.RelativePath ?? "");
                if (examples.TryGetValue(key, out var example))
                {
                    Attach(operation.RequestBody?.Content, example.Request, operation.OperationId!);
                    Attach(operation.Responses?.GetValueOrDefault(example.Status)?.Content, example.Response, operation.OperationId!);
                }
                return Task.CompletedTask;
            }
        );
        o.AddDocumentTransformer(
            (document, _, _) =>
            {
                ShapeDocument(document, mount, confirmationHeader, docs);
                return Task.CompletedTask;
            }
        );
    }

    // An enum is an open string whose known values the serializer writes; a ref is its string. An object leaves markers
    // for the members its serializer omits when null and the ones it refuses when absent.
    private static Task ShapeSchema(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken _)
    {
        var type = Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ?? context.JsonTypeInfo.Type;
        if (type.IsEnum)
        {
            schema.Type = context.JsonTypeInfo.Type == type ? JsonSchemaType.String : JsonSchemaType.String | JsonSchemaType.Null;
            schema.Enum = null;
            schema.Extensions ??= new Dictionary<string, IOpenApiExtension>(StringComparer.Ordinal);
            schema.Extensions[KnownValuesExtension] = new JsonNodeExtension(KnownValues(type));
            schema.Extensions[EnumMarker] = new JsonNodeExtension(JsonValue.Create(type.Name));
        }
        else if (type == typeof(JobRef) || type == typeof(AlertRef) || type == typeof(WorkerRef))
        {
            schema.Type = context.JsonTypeInfo.Type == type ? JsonSchemaType.String : JsonSchemaType.String | JsonSchemaType.Null;
        }
        else if (type == typeof(JsonElement))
        {
            schema.Description = AnyJson().Description;
        }
        else if (context.JsonTypeInfo.Kind == JsonTypeInfoKind.Object)
        {
            var omitted = context.JsonTypeInfo.Properties.Where(Omitted).Select(p => (JsonNode)p.Name).ToArray();
            var required = context.JsonTypeInfo.Properties.Where(p => p.IsRequired).Select(p => (JsonNode)p.Name).ToArray();
            schema.Extensions ??= new Dictionary<string, IOpenApiExtension>(StringComparer.Ordinal);
            schema.Extensions[OmittedMarker] = new JsonNodeExtension(new JsonArray(omitted));
            schema.Extensions[RequiredMarker] = new JsonNodeExtension(new JsonArray(required));
        }
        return Task.CompletedTask;
    }

    private static bool Omitted(JsonPropertyInfo property) =>
        property
            .AttributeProvider?.GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: true)
            .OfType<JsonIgnoreAttribute>()
            .Any(a => a.Condition is JsonIgnoreCondition.WhenWritingNull or JsonIgnoreCondition.WhenWritingDefault) == true;

    private static void ShapeDocument(OpenApiDocument document, Mount mount, string? confirmationHeader, SchemaDocs docs)
    {
        var schemas = document.Components?.Schemas ?? new Dictionary<string, IOpenApiSchema>();
        UseEnumComponents(schemas);
        InlineAnyJson(document, schemas);
        // A ref is one non-null string component, as a code is; a nullable member says so beside its reference.
        foreach (var name in new[] { nameof(JobRef), nameof(AlertRef), nameof(WorkerRef) })
        {
            if (schemas.TryGetValue(name, out var component) && component is OpenApiSchema reference)
            {
                reference.Type = JsonSchemaType.String;
            }
        }

        var (requests, responses) = Directions(document);
        foreach (var (name, entry) in schemas)
        {
            // Every object, members or none: an empty request still refuses unknown members.
            if (entry is not OpenApiSchema schema || schema.Type?.HasFlag(JsonSchemaType.Object) != true)
            {
                continue;
            }

            var properties = schema.Properties ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
            var omitted = Marker(schema, OmittedMarker);
            var jsonRequired = Marker(schema, RequiredMarker);
            if (requests.Contains(name) && responses.Contains(name))
            {
                ShapeEnvelope(name, schema, properties);
                continue;
            }

            if (responses.Contains(name))
            {
                // Every member the serializer always writes is present; one it omits when null is optional and never null.
                foreach (var member in omitted)
                {
                    properties[member] = WithoutNull(properties[member]);
                }
                schema.Required = new HashSet<string>(properties.Keys.Where(key => !omitted.Contains(key)), StringComparer.Ordinal);
            }
            else if (requests.Contains(name))
            {
                // A request member is required only where the runtime refuses its absence; unknown members are refused.
                var handlerRequired = HandlerRequiredMembers.GetValueOrDefault(name, []);
                foreach (var member in handlerRequired)
                {
                    properties[member] = WithoutNull(properties[member]);
                }
                schema.Required = new HashSet<string>(jsonRequired.Concat(handlerRequired), StringComparer.Ordinal);
                schema.AdditionalPropertiesAllowed = false;
            }
        }

        foreach (var schema in schemas.Values.OfType<OpenApiSchema>())
        {
            schema.Extensions?.Remove(OmittedMarker);
            schema.Extensions?.Remove(RequiredMarker);
            schema.Extensions?.Remove(EnumMarker);
        }

        if (schemas.TryGetValue("SaveCalendarDraftRequest", out var draft) && draft is OpenApiSchema { Properties: { } draftProperties })
        {
            // The endpoint requires a JSON object; the signal body and job input stay any JSON value.
            draftProperties["definition"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Description = "The calendar definition: a JSON object whose \"kind\" names its shape.",
            };
        }

        // An outbox verb's ids are absent for every quarantined row or name at least one; the operation refuses an empty list.
        foreach (var name in new[] { "DiscardOutboxRequest", "RequeueOutboxRequest" })
        {
            if (
                schemas.TryGetValue(name, out var verb)
                && verb is OpenApiSchema { Properties: { } verbProperties }
                && verbProperties.TryGetValue("outboxIds", out var outboxIds)
                && outboxIds is OpenApiSchema idList
            )
            {
                idList.MinItems = 1;
            }
        }

        if (
            schemas.TryGetValue("ProblemDetails", out var problem)
            && problem is OpenApiSchema { Properties: { } problemProperties } problemSchema
        )
        {
            schemas[nameof(EnqueueRejectionReason)] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Description = "Why an enqueue was refused.",
                Extensions = new Dictionary<string, IOpenApiExtension>(StringComparer.Ordinal)
                {
                    [KnownValuesExtension] = new JsonNodeExtension(
                        new JsonArray([.. Enum.GetValues<EnqueueRejectionReason>().Select(r => (JsonNode)ReasonCode(r))])
                    ),
                },
            };
            problemProperties["reasonCode"] = new OpenApiSchemaReference(nameof(EnqueueRejectionReason));
            // Every problem Acta writes names its status; the other members depend on the status and the host.
            problemProperties["status"] = WithoutNull(problemProperties["status"]);
            problemSchema.Required = new HashSet<string>(["status"], StringComparer.Ordinal);
        }

        // The capabilities' provider is a string the endpoint spells from DbProvider, a code like any other.
        if (
            schemas.TryGetValue("GetCapabilitiesResponse", out var capabilities)
            && capabilities is OpenApiSchema { Properties: { } capabilityProperties }
        )
        {
            schemas[nameof(DbProvider)] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Extensions = new Dictionary<string, IOpenApiExtension>(StringComparer.Ordinal)
                {
                    [KnownValuesExtension] = new JsonNodeExtension(
                        new JsonArray([.. Enum.GetValues<DbProvider>().Select(p => (JsonNode)GetCapabilitiesEndpoint.ProviderCode(p))])
                    ),
                },
            };
            capabilityProperties["provider"] = new OpenApiSchemaReference(nameof(DbProvider));
        }

        // Once every member has its final schema, so no later replacement drops its text.
        docs.Describe(schemas);
        DescribeKnownValues(schemas);

        if (confirmationHeader is not null)
        {
            document.Components!.Parameters ??= new Dictionary<string, IOpenApiParameter>(StringComparer.Ordinal);
            schemas[ControlSchema] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Description = "The one value a control's confirmation header carries.",
                Enum = [(JsonNode)"true"],
            };
            document.Components.Parameters[ControlParameter] = new OpenApiParameter
            {
                Name = confirmationHeader,
                In = ParameterLocation.Header,
                Required = true,
                Description = "Confirms a control call. The value is true; a missing header or any other value answers 400.",
                Schema = new OpenApiSchemaReference(ControlSchema),
            };
        }

        // Paths read below the mount's API root, which the one server names, so a client's base URL picks the mount.
        var paths = new OpenApiPaths();
        foreach (var (path, item) in document.Paths)
        {
            paths[path.StartsWith(mount.ApiRoot + "/", StringComparison.Ordinal) ? path[mount.ApiRoot.Length..] : path] = item;
        }
        document.Paths = paths;
        document.Servers =
        [
            new OpenApiServer
            {
                Url = mount.ApiRoot,
                Description = "The mount's API root; a host that maps Acta elsewhere serves it there.",
            },
        ];

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, item) in document.Paths)
        {
            foreach (var (method, operation) in item.Operations ?? [])
            {
                // In status order, whichever of the group's or the route's declarations made each one, each saying what its
                // status means for the route.
                var byStatus = new OpenApiResponses();
                foreach (var (code, response) in (operation.Responses ?? []).OrderBy(r => r.Key, StringComparer.Ordinal))
                {
                    if (
                        response is OpenApiResponse declared
                        && ResponseDescription(code, method == HttpMethod.Get, declared, schemas) is { } text
                    )
                    {
                        declared.Description = text;
                    }
                    byStatus[code] = response;
                }
                operation.Responses = byStatus;

                if (string.IsNullOrEmpty(operation.OperationId) || !ids.Add(operation.OperationId))
                {
                    throw new InvalidOperationException(
                        $"{method} {path}: operation id '{operation.OperationId}' is empty or already taken."
                    );
                }
            }
        }

        foreach (var tag in document.Tags ?? new HashSet<OpenApiTag>())
        {
            tag.Description ??= TagDescriptions.GetValueOrDefault(tag.Name!);
        }
    }

    /// <summary>The payload envelope's members a response alone carries: readOnly in the document, refused in a request.</summary>
    public static readonly string[] EnvelopeResponseOnly = ["formatName", "byteLength", "truncated", "malformed"];

    private const string EnvelopeDescription =
        "A payload inside a JSON body, in a request and in a response. A JSON payload is json, the value itself; any other "
        + "format is base64, its bytes, with formatId. A request sends exactly one of json and base64: json alone or with "
        + "formatId 1, or base64 with formatId 2 (bytes) or 128 to 255 (an application's own); the readOnly members are "
        + "refused. A response always carries formatId and formatName, with json or base64, or with base64 and malformed "
        + "true for stored JSON that is not one JSON value, or, past the server's inline limit, with byteLength and "
        + "truncated true in place of the body. No envelope is no payload.";

    // The payload envelope is the one schema both directions share. Which of its members appear depends on the payload,
    // so none is required and the description says which; a member is never null in either direction.
    private static void ShapeEnvelope(string name, OpenApiSchema schema, IDictionary<string, IOpenApiSchema> properties)
    {
        if (name != nameof(JobPayloadBody))
        {
            throw new InvalidOperationException($"{name} is both a request and a response; say which members each direction requires.");
        }

        foreach (var (member, property) in properties.ToList())
        {
            properties[member] = WithoutNull(property);
            if (EnvelopeResponseOnly.Contains(member) && properties[member] is OpenApiSchema responseOnly)
            {
                responseOnly.ReadOnly = true;
            }
        }
        properties["json"] = new OpenApiSchema { Description = "A JSON payload: the value itself, any JSON value." };
        schema.Required = null;
        schema.AdditionalPropertiesAllowed = false;
        schema.Description = EnvelopeDescription;
    }

    // One schema per enum, a non-null string under the enum's name, and every use a reference to it: nullable where the
    // member is. The generator inlines some nullable members and makes a component nullable when its first use was.
    private static void UseEnumComponents(IDictionary<string, IOpenApiSchema> schemas)
    {
        foreach (var component in schemas.Values.OfType<OpenApiSchema>().Where(schema => EnumName(schema) is not null))
        {
            component.Type = JsonSchemaType.String;
        }

        foreach (var schema in schemas.Values.OfType<OpenApiSchema>().ToList())
        {
            foreach (var (member, property) in schema.Properties?.ToList() ?? [])
            {
                if (property is OpenApiSchema inline && EnumName(inline) is { } name)
                {
                    if (!schemas.ContainsKey(name))
                    {
                        schemas[name] = new OpenApiSchema
                        {
                            Type = JsonSchemaType.String,
                            Extensions = new Dictionary<string, IOpenApiExtension>(inline.Extensions!, StringComparer.Ordinal),
                        };
                    }
                    IOpenApiSchema reference = new OpenApiSchemaReference(name);
                    schema.Properties![member] =
                        inline.Type?.HasFlag(JsonSchemaType.Null) == true
                            ? new OpenApiSchema
                            {
                                OneOf = [new OpenApiSchema { Type = JsonSchemaType.Null }, reference],
                                Description = inline.Description,
                            }
                            : reference;
                }
            }
        }
    }

    // Raw JSON is any JSON value, written where it is used rather than as a component a client would generate a type for.
    private static void InlineAnyJson(OpenApiDocument document, IDictionary<string, IOpenApiSchema> schemas)
    {
        if (!schemas.Remove(nameof(JsonElement)))
        {
            return;
        }

        static bool IsAnyJson(IOpenApiSchema? schema) => schema is OpenApiSchemaReference { Reference.Id: nameof(JsonElement) };
        foreach (
            var media in document
                .Paths.Values.SelectMany(item => item.Operations?.Values ?? Enumerable.Empty<OpenApiOperation>())
                .SelectMany(operation => operation.RequestBody?.Content?.Values ?? Enumerable.Empty<OpenApiMediaType>())
                .Where(media => IsAnyJson(media.Schema))
        )
        {
            media.Schema = AnyJson();
        }
        foreach (var schema in schemas.Values.OfType<OpenApiSchema>())
        {
            foreach (var (member, _) in schema.Properties?.Where(property => IsAnyJson(property.Value)).ToList() ?? [])
            {
                schema.Properties![member] = AnyJson();
            }
        }
    }

    private static OpenApiSchema AnyJson() => new() { Description = "Any JSON value, kept as sent." };

    private static void Attach(IDictionary<string, OpenApiMediaType>? content, JsonNode? value, string name)
    {
        if (value is not null && content?.TryGetValue("application/json", out var media) == true)
        {
            media.Examples = new Dictionary<string, IOpenApiExample>(StringComparer.Ordinal)
            {
                [name] = new OpenApiExample { Value = value },
            };
        }
    }

    private const string StoredJsonDescription = "The stored JSON payload, one JSON value.";

    // A code reads as well as it parses: its description lists the known values, with what each means where its family's
    // [Code] says so, and ends saying others may appear; its example is the first value that is not "unspecified".
    private static void DescribeKnownValues(IDictionary<string, IOpenApiSchema> schemas)
    {
        foreach (var (name, entry) in schemas)
        {
            if (
                entry is not OpenApiSchema schema
                || schema.Extensions?.TryGetValue(KnownValuesExtension, out var extension) != true
                || extension is not JsonNodeExtension { Node: JsonArray known }
            )
            {
                continue;
            }

            var values = known.Select(value => value!.GetValue<string>()).ToList();
            var family = typeof(IJobs)
                .Assembly.GetTypes()
                .Concat(typeof(DashboardJsonContext).Assembly.GetTypes())
                .FirstOrDefault(type => type.IsEnum && type.Name == name);
            var meanings = family is null
                ? []
                : Enum.GetNames(family)
                    .Select(member => family.GetField(member)!.GetCustomAttribute<CodeAttribute>()?.Description)
                    .ToList();
            var list =
                meanings.Count == values.Count && meanings.All(meaning => meaning is not null)
                    ? "Known values:\n" + string.Join("\n", values.Select((value, i) => $"- {value}: {meanings[i]}"))
                    : $"Known values: {string.Join(", ", values)}.";
            schema.Description = string.Join(
                "\n\n",
                new[] { schema.Description, list, OtherValuesMayAppear }.Where(text => !string.IsNullOrEmpty(text))
            );
            // OpenAPI 3.1 takes a schema's examples as JSON Schema's array; the single example keyword is deprecated there.
            schema.Examples = [JsonValue.Create(values.Find(value => value != "unspecified") ?? values[0])];
        }
    }

    private static string? EnumName(OpenApiSchema schema) =>
        schema.Extensions?.TryGetValue(EnumMarker, out var value) == true && value is JsonNodeExtension { Node: JsonValue name }
            ? name.GetValue<string>()
            : null;

    // The component schemas reachable from request bodies and from responses, following references.
    private static (HashSet<string> Requests, HashSet<string> Responses) Directions(OpenApiDocument document)
    {
        var requests = new HashSet<string>(StringComparer.Ordinal);
        var responses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in document.Paths.Values.SelectMany(item => item.Operations?.Values ?? Enumerable.Empty<OpenApiOperation>()))
        {
            foreach (var media in operation.RequestBody?.Content?.Values ?? Enumerable.Empty<OpenApiMediaType>())
            {
                Reach(media.Schema, document, requests);
            }
            foreach (
                var media in (operation.Responses?.Values ?? Enumerable.Empty<IOpenApiResponse>()).SelectMany(response =>
                    response.Content?.Values ?? Enumerable.Empty<OpenApiMediaType>()
                )
            )
            {
                Reach(media.Schema, document, responses);
            }
        }
        return (requests, responses);
    }

    private static void Reach(IOpenApiSchema? schema, OpenApiDocument document, HashSet<string> reached)
    {
        switch (schema)
        {
            case null:
                return;
            case OpenApiSchemaReference reference when reference.Reference.Id is { } id:
                if (reached.Add(id) && document.Components?.Schemas?.TryGetValue(id, out var target) == true)
                {
                    Reach(target, document, reached);
                }
                return;
        }

        foreach (
            var child in (schema.Properties?.Values ?? []).Concat(schema.OneOf ?? []).Concat(schema.AnyOf ?? []).Concat(schema.AllOf ?? [])
        )
        {
            Reach(child, document, reached);
        }
        Reach(schema.Items, document, reached);
        Reach(schema.AdditionalProperties, document, reached);
    }

    private static HashSet<string> Marker(OpenApiSchema schema, string marker) =>
        schema.Extensions?.TryGetValue(marker, out var value) == true && value is JsonNodeExtension { Node: JsonArray names }
            ? names.Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal)
            : [];

    // The member without its null: a type array loses null, and a nullable reference becomes the reference itself.
    private static IOpenApiSchema WithoutNull(IOpenApiSchema member) =>
        member switch
        {
            OpenApiSchema { OneOf: { Count: 2 } branches } when branches.Any(b => b is OpenApiSchema { Type: JsonSchemaType.Null }) =>
                branches.First(b => b is not OpenApiSchema { Type: JsonSchemaType.Null }),
            OpenApiSchema { Type: { } type } inline when type.HasFlag(JsonSchemaType.Null) => Without(inline, type & ~JsonSchemaType.Null),
            _ => member,
        };

    private static OpenApiSchema Without(OpenApiSchema schema, JsonSchemaType type)
    {
        schema.Type = type;
        return schema;
    }

    private static Type CodeFamily(string codeKind) =>
        typeof(IJobs).Assembly.GetTypes().Single(t => t.IsEnum && t.GetCustomAttribute<CodeKindAttribute>()?.CodeKind == codeKind);

    private static T? Metadata<T>(OpenApiOperationTransformerContext context)
        where T : class => context.Description.ActionDescriptor.EndpointMetadata.OfType<T>().LastOrDefault();

    private const string DocumentDescription =
        "Acta's operator API, the one the dashboard uses. Lists page by cursor: pass a page's nextCursor back as cursor "
        + "until hasMore is false; a cursor is tied to its query. Every POST, PUT, and DELETE is a control, mapped only "
        + "where the host enables controls, and where the host requires it the call carries the confirmation header the "
        + "operation lists, with the value true. A control whose request object is optional acts with its defaults when the "
        + "request has no body; one that declares application/json and sends no bytes answers 400, so send {} or leave the "
        + "content type off. A signal raise takes any JSON value instead, and no bytes, with or without the content type, "
        + "raise a presence-only signal. POST /jobs answers 201 when it inserted the job and 200 when a deduplication key matched an "
        + "existing one, with the job's URL in Location either way. A string carrying x-acta-known-values is an open set: "
        + "compare against the known values and keep any other value as it is. The document declares no security scheme: "
        + "the host adds its own authentication, which a client sends as the host asks.";

    /// <summary>The framework-shaped body declaration the generator reads, built from a RequestBodyDoc.</summary>
    private sealed class DeclaredRequestBody(RequestBodyDoc doc) : IAcceptsMetadata
    {
        public IReadOnlyList<string> ContentTypes { get; } = ["application/json"];

        public Type? RequestType { get; } = doc.BodyType;

        public bool IsOptional { get; } = doc.Optional;
    }
}
