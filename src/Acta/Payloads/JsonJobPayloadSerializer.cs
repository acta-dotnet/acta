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
public sealed class JsonJobPayloadSerializer(JsonSerializerOptions options) : IJobPayloadSerializer
{
    /// <summary>
    /// Framework defaults: the <see cref="JsonSerializerDefaults.Web"/> baseline (camelCase names plus
    /// <see cref="JsonNumberHandling.AllowReadingFromString"/>); reads are case-insensitive for property names and
    /// enum strings; enums emit as camelCase strings but tolerate numbers; trailing commas and <c>//</c> comments are
    /// accepted on input. <see cref="JsonSerializerOptions.DefaultIgnoreCondition"/> is pinned to
    /// <see cref="JsonIgnoreCondition.Never"/> so nulls and defaults round-trip, letting the receiver tell a null field
    /// from an omitted one.
    /// </summary>
    public static JsonSerializerOptions DefaultJsonOptions { get; } = BuildDefaults(resolver: null);

    /// <summary>
    /// Shared instance using <see cref="DefaultJsonOptions"/>, reused by <see cref="JobPayload.Json{T}(T)"/>
    /// so callers that don't need custom options don't allocate a fresh serializer per call.
    /// </summary>
    public static JsonJobPayloadSerializer Default { get; } = new();

    private readonly JsonSerializerOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public JsonJobPayloadSerializer()
        : this(DefaultJsonOptions) { }

    /// <summary>
    /// Builds a serializer whose options resolve payload type metadata from <paramref name="resolver"/>
    /// (an app-supplied source-generated <c>JsonSerializerContext</c>), so payload (de)serialization needs
    /// no reflection under Native AOT. Wire it via <c>IActaBuilder.UseJsonPayloads(...)</c>. The framework
    /// wire shape (camelCase, string enums) still applies; the resolver supplies only the type metadata.
    /// </summary>
    public static JsonJobPayloadSerializer WithResolver(IJsonTypeInfoResolver resolver) =>
        new(BuildDefaults(resolver ?? throw new ArgumentNullException(nameof(resolver))));

    /// <summary>
    /// The reflection members below (string-enum converter, DefaultJsonTypeInfoResolver) run only
    /// inside the IsReflectionEnabledByDefault branch - never under reflection-off Native AOT,
    /// where the branch is dead and trimmed. The analyzer can't evaluate the feature switch, so
    /// suppress here; the guarantee is the feature guard, not these attributes.
    /// </summary>
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
            options.TypeInfoResolver = ReflectionFreeResolver(resolver);
        }
        options.MakeReadOnly();
        return options;
    }

    /// <summary>
    /// The resolver a reflection-off (Native AOT) build reads metadata from: the app-supplied resolver first, then
    /// <see cref="ActaSystemJobJsonContext"/>, so a value Acta persists itself never depends on the app registering
    /// its type. Without an app resolver the framework context stands alone, so MakeReadOnly never installs the
    /// reflection resolver, which throws under reflection-off, and an unlisted type fails with a "no metadata" error.
    /// </summary>
    internal static IJsonTypeInfoResolver ReflectionFreeResolver(IJsonTypeInfoResolver? resolver) =>
        resolver is null ? ActaSystemJobJsonContext.Default : JsonTypeInfoResolver.Combine(resolver, ActaSystemJobJsonContext.Default);

    public JobPayloadFormat Format => JobPayloadFormat.Json;

    /// <summary>
    /// Resolves T's metadata from the configured options and uses the JsonTypeInfo&lt;T&gt;
    /// overloads, which are free of the reflection Requires* attributes the generic
    /// JsonSerializer.Serialize/Deserialize overloads carry. Under reflection-on the default
    /// resolver supplies the metadata; under reflection-off the app-supplied source-generated
    /// resolver does. An unresolved T throws a clear "no metadata" error.
    /// </summary>
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

/// <summary>
/// Source-generated metadata for every value Acta persists itself through the job payload serializer: the
/// <c>sys.alerts</c> cursor and its poison records (a string), and the <c>sys.retention</c> jobs cursor and a bounded
/// group wait's deadline (each a long). A reflection-off build chains it after the app's resolver
/// (<see cref="JsonJobPayloadSerializer.ReflectionFreeResolver"/>).
/// </summary>
[JsonSerializable(typeof(AlertsCursor))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(long))]
internal sealed partial class ActaSystemJobJsonContext : JsonSerializerContext;
