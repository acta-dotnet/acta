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

    /// <summary>The extension carrying the permission an operation needs, one of <see cref="ActaPermissions"/>.</summary>
    public const string PermissionExtension = "x-acta-permission";

    /// <summary>The sentence every code schema's description ends with.</summary>
    public const string OtherValuesMayAppear = "Other values may appear: keep a value this client does not know as it is.";

    /// <summary>The version of the HTTP contract under <c>/v1</c>, not the package version.</summary>
    private const string ContractVersion = "1.0.0";

    // Markers the schema transformer leaves for the document transformer, which alone knows a schema's direction.
    private const string OmittedMarker = "x-acta-omitted-when-null";
    private const string RequiredMarker = "x-acta-json-required";
    private const string NotNullMarker = "x-acta-not-null";
    private const string EnumMarker = "x-acta-enum";

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

    /// <summary>
    /// One Acta mount of the generating host: its pattern, whether it maps the controls, whether those require
    /// confirmation, and the confirmation header's name when it is not the default.
    /// </summary>
    public sealed record Mount(string Pattern, bool Controls, bool RequireConfirmation = true, string? ConfirmationHeaderName = null)
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
        // The generator reads the host's options; requests and responses go through ActaApiJsonContext, whose numbers
        // are strict. The web default would document every integer as integer-or-string, which the server refuses.
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.NumberHandling = ActaApiJsonContext.Default.Options.NumberHandling
        );
        var fakes = StagedFakes.Create();
        builder.Services.AddSingleton<IJobs>(fakes.Jobs);
        builder.Services.AddSingleton<IActaOperations>(fakes);
        // The staged provider is SQLite, whose tables live in main.
        builder.Services.AddSingleton(new SqlProviderOptions { Schema = "main" });
        // Filled once the host runs and before any document is read; the operations are the same under every mount.
        var examples = new Dictionary<string, OpenApiExamples.Example>(StringComparer.Ordinal);
        foreach (var mount in mounts)
        {
            builder.Services.AddOpenApi(mount.DocumentName, o => Configure(o, mount, examples));
        }

        var app = builder.Build();
        foreach (var mount in mounts)
        {
            // The API alone, at the URLs a dashboard mount gives it: the dashboard's pages are no part of the contract.
            var acta = app.MapActaApi(
                mount.Pattern + "/api",
                o =>
                {
                    o.EnableControls = mount.Controls;
                    o.RequireControlConfirmation = mount.RequireConfirmation;
                    o.ControlConfirmationHeaderName = mount.ConfirmationHeaderName ?? o.ControlConfirmationHeaderName;
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

    /// <summary>How <c>ActaApiJsonContext</c> writes one enum member, the only authority on it; a name it cannot write throws.</summary>
    public static string WireName(Type enumType, string memberName) =>
        JsonSerializer.Serialize(Enum.Parse(enumType, memberName), enumType, ActaApiJsonContext.Default.Options).Trim('"');

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
                        QueryParameterKind.Long => new OpenApiSchema
                        {
                            Type = JsonSchemaType.Integer,
                            Format = "int64",
                            Minimum = doc.Minimum?.ToString(CultureInfo.InvariantCulture),
                        },
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
        // A route declares its operation id and permission (ActaOperationAttribute); the description names the permission.
        o.AddOperationTransformer(
            (operation, context, _) =>
            {
                if (Metadata<ActaOperationAttribute>(context) is { } declared)
                {
                    operation.OperationId = declared.OperationId;
                    operation.Extensions ??= new Dictionary<string, IOpenApiExtension>(StringComparer.Ordinal);
                    operation.Extensions[PermissionExtension] = new JsonNodeExtension(JsonValue.Create(declared.Permission));
                    operation.Description = string.Join(
                        "\n\n",
                        new[] { operation.Description, $"Needs the {declared.Permission} permission." }.Where(text =>
                            !string.IsNullOrEmpty(text)
                        )
                    );
                }
                return Task.CompletedTask;
            }
        );
        // A control on a mount that requires confirmation names the mount's header, which the introduction's rule names.
        string? confirmationHeader = null;
        o.AddOperationTransformer(
            (_, context, _) =>
            {
                confirmationHeader ??= Metadata<ControlEndpointDoc>(context)?.ConfirmationHeaderName;
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
            (operation, _, _) =>
            {
                if (operation.OperationId is { } id && examples.TryGetValue(id, out var example))
                {
                    Attach(operation.RequestBody?.Content, example.Request, id);
                    Attach(operation.Responses?.GetValueOrDefault(example.Status)?.Content, example.Response, id);
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
    // for the members its serializer omits when null, the ones it refuses when absent, and the get-only ones whose getter
    // never returns null: the generator types a member nullable when its setter may take null, and with no setter at all
    // it always does.
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
            // A member of raw JSON says what it carries from its record's docs; only a bare body says "any value".
            schema.Description = context.JsonPropertyInfo is null ? AnyJson().Description : null;
        }
        else if (context.JsonTypeInfo.Kind == JsonTypeInfoKind.Object)
        {
            var omitted = context.JsonTypeInfo.Properties.Where(Omitted).Select(p => (JsonNode)p.Name).ToArray();
            var required = context.JsonTypeInfo.Properties.Where(p => p.IsRequired).Select(p => (JsonNode)p.Name).ToArray();
            var notNull = context
                .JsonTypeInfo.Properties.Where(p => p is { Get: not null, Set: null, IsGetNullable: false })
                .Select(p => (JsonNode)p.Name)
                .ToArray();
            schema.Extensions ??= new Dictionary<string, IOpenApiExtension>(StringComparer.Ordinal);
            schema.Extensions[OmittedMarker] = new JsonNodeExtension(new JsonArray(omitted));
            schema.Extensions[RequiredMarker] = new JsonNodeExtension(new JsonArray(required));
            schema.Extensions[NotNullMarker] = new JsonNodeExtension(new JsonArray(notNull));
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
            foreach (var member in Marker(schema, NotNullMarker))
            {
                properties[member] = WithoutNull(properties[member]);
            }

            // A response requires every member the serializer always writes; a request requires only the members the runtime
            // refuses without. One schema says one required set and one nullability, so a schema serves both directions only
            // where the two required sets are the same and no handler requires a member: the request would drop that member's
            // null, which a response may write.
            var responseRequired = properties.Keys.Where(key => !omitted.Contains(key)).ToHashSet(StringComparer.Ordinal);
            var handlerRequired = HandlerRequiredMembers.GetValueOrDefault(name, []);
            var requestRequired = jsonRequired.Concat(handlerRequired).ToHashSet(StringComparer.Ordinal);
            if (
                requests.Contains(name)
                && responses.Contains(name)
                && (!requestRequired.SetEquals(responseRequired) || handlerRequired.Length > 0)
            )
            {
                throw new InvalidOperationException(
                    $"{name} is both a request and a response, and the two directions disagree on its members; give each its own record."
                );
            }

            if (responses.Contains(name))
            {
                // A member the serializer omits when null is optional and never null.
                foreach (var member in omitted)
                {
                    properties[member] = WithoutNull(properties[member]);
                }
                schema.Required = responseRequired;
            }
            if (requests.Contains(name))
            {
                // Unknown members are refused.
                foreach (var member in handlerRequired)
                {
                    properties[member] = WithoutNull(properties[member]);
                }
                schema.Required = requestRequired;
                schema.AdditionalPropertiesAllowed = false;
            }
        }

        foreach (var schema in schemas.Values.OfType<OpenApiSchema>())
        {
            schema.Extensions?.Remove(OmittedMarker);
            schema.Extensions?.Remove(RequiredMarker);
            schema.Extensions?.Remove(NotNullMarker);
            schema.Extensions?.Remove(EnumMarker);
        }

        // A calendar definition is a JSON object both ways: the draft endpoint requires one, and the read embeds the stored
        // one, described from its record's docs. The signal body and job input stay any JSON value.
        foreach (
            var (name, text) in new (string Name, string? Text)[]
            {
                ("SaveCalendarDraftRequest", "The calendar definition: a JSON object whose \"kind\" names its shape."),
                ("GetCalendarResponse", null),
            }
        )
        {
            if (schemas.TryGetValue(name, out var calendar) && calendar is OpenApiSchema { Properties: { } calendarProperties })
            {
                calendarProperties["definition"] = new OpenApiSchema { Type = JsonSchemaType.Object, Description = text };
            }
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
            problemProperties["reasonCode"] = new OpenApiSchemaReference(nameof(EnqueueRejectionReason))
            {
                Description = "Why an enqueue was refused; only that answer carries it.",
            };
            // Every problem Acta writes names its status; the other members depend on the status and the host.
            problemProperties["status"] = WithoutNull(problemProperties["status"]);
            problemSchema.Required = new HashSet<string>(["status"], StringComparer.Ordinal);
            // The problem document is ASP.NET's type, so its text is written here, not read from Acta's XML docs.
            problemSchema.Description = "Why a call failed, as an RFC 9457 problem document.";
            foreach (var (member, text) in ProblemMembers)
            {
                if (problemProperties.TryGetValue(member, out var property) && property is OpenApiSchema described)
                {
                    described.Description = text;
                }
            }
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

        // The confirmation is no parameter of any operation, since a JSON body or a bearer token passes it as well as the
        // header does; the introduction states the rule once.
        if (confirmationHeader is not null)
        {
            document.Info.Description += "\n\n" + ConfirmationRule(confirmationHeader);
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
                        && (
                            RouteResponses.GetValueOrDefault((operation.OperationId ?? "", code))
                            ?? ResponseDescription(code, method == HttpMethod.Get, declared, schemas)
                        )
                            is { } text
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

    private static readonly Dictionary<string, string> ProblemMembers = new(StringComparer.Ordinal)
    {
        ["type"] = "A URI that identifies the kind of problem.",
        ["title"] = "A short summary of the kind of problem.",
        ["status"] = "The HTTP status code of this answer.",
        ["detail"] = "What went wrong in this call; it names the parameter or member at fault when there is one.",
        ["instance"] = "A URI for this occurrence of the problem, when the server sets one.",
    };

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
    // A body says so itself; a member is described later from its record's docs, as every member is.
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
                schema.Properties![member] = new OpenApiSchema();
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
                .Concat(typeof(ActaApiJsonContext).Assembly.GetTypes())
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
        + "where the host enables controls. A control whose request object is optional acts with its defaults when the "
        + "request has no body, with or without a JSON content type. A signal raise takes any JSON value instead, and no "
        + "bytes raise a presence-only signal. POST /jobs answers 201 when it inserted the job and 200 when a deduplication key matched an "
        + "existing one, with the job's URL in Location either way. A string carrying x-acta-known-values is an open set: "
        + "compare against the known values and keep any other value as it is. The document declares no security scheme: "
        + "the host adds its own authentication, which a client sends as the host asks. Each operation names the permission it "
        + "needs in x-acta-permission; where the host turns permission checks on, a call with no credential answers 401, and one "
        + "whose caller lacks the permission answers 403.";

    // The introduction's paragraph for a mount whose controls require confirmation, naming its header.
    private static string ConfirmationRule(string header) =>
        $"A call that changes something must show that it is no cross-site form post, in one of three ways: the header {header}: true, "
        + "a JSON content type, or a Bearer authorization header. A client that sends a JSON body or a bearer token passes without "
        + "doing anything more; for a call with no body and no bearer token, send the header.";

    /// <summary>The framework-shaped body declaration the generator reads, built from a RequestBodyDoc.</summary>
    private sealed class DeclaredRequestBody(RequestBodyDoc doc) : IAcceptsMetadata
    {
        public IReadOnlyList<string> ContentTypes { get; } = ["application/json"];

        public Type? RequestType { get; } = doc.BodyType;

        public bool IsOptional { get; } = doc.Optional;
    }
}
