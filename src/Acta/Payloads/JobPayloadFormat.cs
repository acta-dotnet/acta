namespace Acta;

/// <summary>
/// Durable payload format: the byte id and kebab-case name of an encoder pipeline. Id 0 means no
/// payload, 1 through 127 are reserved for framework built-ins, and 128 through 255 are available for
/// consumer-defined formats. Construction is gated through <see cref="Custom"/> (public) and
/// <see cref="BuiltIn"/> (internal).
/// </summary>
/// <remarks>
/// The backing field is a nullable string so <c>default(JobPayloadFormat)</c> is a valid value
/// equivalent to <see cref="None"/>; <see cref="Name"/> normalizes the null backing to <c>"none"</c>
/// at the public surface. Runtime dispatch keys on <see cref="Id"/>; the synthesised record-struct
/// equality exists for source-code ergonomics but is not used on the hot path.
/// </remarks>
public readonly record struct JobPayloadFormat
{
    public byte Id { get; }

    /// <summary>
    /// Non-null at the public surface. Returns <c>"none"</c> when the value is <c>default</c>
    /// (zero id, null backing field) and the validated kebab-case name otherwise.
    /// </summary>
    public string Name => IsNone && string.IsNullOrEmpty(field) ? "none" : field ?? "";

    public bool IsNone => Id == 0;
    public bool IsBuiltIn => Id is > 0 and < 128;
    public bool IsCustom => Id >= 128;

    private JobPayloadFormat(byte id, string name)
    {
        Id = id;
        Name = name;
    }

    internal static JobPayloadFormat BuiltIn(byte id, string name)
    {
        if (id is 0 or >= 128)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "Built-in payload formats must use ids 1..127.");
        }

        IdentifierSyntax.ValidateDottedKebab(name, nameof(name));
        return new JobPayloadFormat(id, name);
    }

    /// <summary>
    /// Creates a custom payload format with the given id (128..255) and kebab-case name (max 64 chars).
    /// </summary>
    public static JobPayloadFormat Custom(byte id, string name)
    {
        if (id < 128)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "Custom payload formats must use ids 128..255.");
        }

        IdentifierSyntax.ValidateDottedKebab(name, nameof(name));
        return new JobPayloadFormat(id, name);
    }

    public override string ToString() => $"{Name}/{Id}";

    /// <summary>No payload: zero id, null backing name, <see cref="Name"/> returns <c>"none"</c>.</summary>
    public static readonly JobPayloadFormat None = default;

    /// <summary>UTF-8 JSON; default for record / class inputs.</summary>
    public static readonly JobPayloadFormat Json = BuiltIn(1, "json");

    /// <summary>Raw <c>byte[]</c> / <c>ReadOnlyMemory&lt;byte&gt;</c> passthrough.</summary>
    public static readonly JobPayloadFormat Bytes = BuiltIn(2, "bytes");

    /// <summary>UTF-8 passthrough: strings and scalar values rendered through <c>Encoding.UTF8</c>.</summary>
    public static readonly JobPayloadFormat Text = BuiltIn(3, "text");

    /// <summary>No-payload format name.</summary>
    public const string NoneName = "none";

    /// <summary>JSON format name.</summary>
    public const string JsonName = "json";

    /// <summary>Bytes format name.</summary>
    public const string BytesName = "bytes";

    /// <summary>Text format name.</summary>
    public const string TextName = "text";

    /// <summary>
    /// Returns the built-in <see cref="JobPayloadFormat"/> for <paramref name="id"/>, or a synthetic
    /// <see cref="Custom"/> stand-in for ids in the consumer range (128..255) the caller hasn't
    /// resolved through the serializer registry. Used by read paths that reconstitute a payload from a
    /// stored format id without going through DI.
    /// </summary>
    /// <remarks>
    /// Custom-range formats can't reconstitute their real name from the id alone without the
    /// registry; the synthetic name round-trips the id so diagnostics still surface the value.
    /// Callers that need the registered name use <see cref="IJobPayloadSerializerRegistry"/>.
    /// </remarks>
    public static JobPayloadFormat ForId(byte id) =>
        id switch
        {
            0 => None,
            1 => Json,
            2 => Bytes,
            3 => Text,
            _ => Custom(id, $"custom-{id}"),
        };
}
