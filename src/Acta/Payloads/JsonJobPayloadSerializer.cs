using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Acta;

/// <summary>
/// Built-in <see cref="IJobPayloadSerializer"/> for <see cref="JobPayloadFormat.Json"/>. Wraps
/// <see cref="System.Text.Json.JsonSerializer"/> with the framework's <see cref="DefaultJsonOptions"/>:
/// Web defaults (camelCase property names, case-insensitive reads), enums as camelCase strings,
/// trailing commas and comments tolerated, numbers readable from JSON strings. Consumer apps needing
/// different behavior either supply their own <see cref="JsonSerializerOptions"/> through the
/// constructor or register a different <see cref="IJobPayloadSerializer"/> for the <c>json</c> format id.
/// </summary>
public sealed class JsonJobPayloadSerializer : IJobPayloadSerializer
{
    /// <summary>
    /// Framework defaults: <see cref="JsonSerializerDefaults.Web"/> baseline (camelCase property naming
    /// plus <see cref="JsonNumberHandling.AllowReadingFromString"/>); reads are case-insensitive for
    /// property names and enum string values; enums emit as camelCase strings but tolerate numeric
    /// fallbacks; trailing commas and <c>//</c> comments are accepted on input.
    /// <see cref="JsonSerializerOptions.DefaultIgnoreCondition"/> is pinned to
    /// <see cref="JsonIgnoreCondition.Never"/> so nulls and default values round-trip, letting the
    /// receiver distinguish a field set to null from an omitted field.
    /// </summary>
    public static JsonSerializerOptions DefaultJsonOptions { get; } = BuildDefaults(resolver: null);

    /// <summary>
    /// Shared instance using <see cref="DefaultJsonOptions"/>, reused by <see cref="JobPayload.Json{T}(T)"/>
    /// so callers that don't need custom options don't allocate a fresh serializer per call.
    /// </summary>
    public static JsonJobPayloadSerializer Default { get; } = new();

    private readonly JsonSerializerOptions _options;

    public JsonJobPayloadSerializer()
        : this(DefaultJsonOptions) { }

    public JsonJobPayloadSerializer(JsonSerializerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Builds a serializer whose options resolve payload type metadata from <paramref name="resolver"/>
    /// (an app-supplied source-generated <c>JsonSerializerContext</c>), so payload (de)serialization needs
    /// no reflection under Native AOT. Wire it via <c>IActaBuilder.UseJsonPayloads(...)</c>. The framework
    /// wire shape (camelCase, string enums) still applies; the resolver supplies only the type metadata.
    /// </summary>
    public static JsonJobPayloadSerializer WithResolver(IJsonTypeInfoResolver resolver) =>
        new(BuildDefaults(resolver ?? throw new ArgumentNullException(nameof(resolver))));

    // The reflection members below (string-enum converter, DefaultJsonTypeInfoResolver) run only inside
    // the IsReflectionEnabledByDefault branch - i.e. never under reflection-off Native AOT, where the
    // branch is dead and trimmed. The analyzer can't evaluate the feature switch, so suppress here; the
    // guarantee is the feature guard, not these attributes.
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Reflection JSON path is guarded by JsonSerializer.IsReflectionEnabledByDefault; unreachable under reflection-off AOT."
    )]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "Reflection JSON path is guarded by JsonSerializer.IsReflectionEnabledByDefault; unreachable under reflection-off AOT."
    )]
    private static JsonSerializerOptions BuildDefaults(IJsonTypeInfoResolver? resolver)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            // Pinned (already set by Web defaults, kept explicit so a runtime-upgrade change to
            // JsonSerializerDefaults can't silently flip our wire shape, and so consumers copying
            // these options into a different baseline see what we depend on).
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        // Read the feature switch DIRECTLY in the condition (not via a local): the trim/AOT analyzers
        // only recognize the reflection branch as guarded - and so stay silent about the reflection-only
        // members inside it - when JsonSerializer.IsReflectionEnabledByDefault is the if-condition itself.
        if (JsonSerializer.IsReflectionEnabledByDefault)
        {
            // The non-generic JsonStringEnumConverter builds per-enum converters by reflection; only safe
            // on the reflection path. Under reflection-off the app's source-generated context
            // (UseStringEnumConverter) supplies the AOT-safe string-enum metadata.
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true));
            // Chain the reflection default so unlisted types still round-trip; an app resolver, when
            // supplied, takes precedence.
            options.TypeInfoResolver = resolver is null
                ? new DefaultJsonTypeInfoResolver()
                : JsonTypeInfoResolver.Combine(resolver, new DefaultJsonTypeInfoResolver());
        }
        else
        {
            // Reflection-off (Native AOT): an app-supplied resolver (via UseJsonPayloads) covers payload
            // types. Always chain the framework's own system-job scalar context after it (e.g. sys.alerts
            // persists a long cursor variable) so a system job never fails for a type the consuming app
            // had no reason to register; app-registered types still take precedence. A resolver-less default
            // still gets a real resolver so MakeReadOnly never installs the reflection resolver (which throws
            // under reflection-off); a stray unlisted type surfaces a clear "no metadata" error.
            options.TypeInfoResolver = resolver is null
                ? ActaSystemJobJsonContext.Default
                : JsonTypeInfoResolver.Combine(resolver, ActaSystemJobJsonContext.Default);
        }
        options.MakeReadOnly();
        return options;
    }

    public JobPayloadFormat Format => JobPayloadFormat.Json;

    // Resolve T's metadata from the configured options and use the JsonTypeInfo<T> overloads, which are
    // free of the reflection Requires* attributes the generic JsonSerializer.Serialize<T>/Deserialize<T>
    // overloads carry. Under reflection-on the default resolver supplies the metadata; under reflection-off
    // the app-supplied source-generated resolver does. An unresolved T throws a clear "no metadata" error.
    public JobPayload Serialize<T>(T value)
    {
        var typeInfo = (JsonTypeInfo<T>)_options.GetTypeInfo(typeof(T));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        return JobPayload.FromBytes(Format, bytes);
    }

    public T Deserialize<T>(JobPayload payload)
    {
        if (payload.Format.Id != Format.Id)
        {
            throw new InvalidOperationException($"JsonJobPayloadSerializer cannot deserialize payload format '{payload.Format}'.");
        }

        var typeInfo = (JsonTypeInfo<T>)_options.GetTypeInfo(typeof(T));
        return JsonSerializer.Deserialize(payload.Data.Span, typeInfo)
            ?? throw new InvalidOperationException("JSON payload deserialized to null.");
    }
}
