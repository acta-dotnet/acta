namespace Acta.Runtime.Kernel;

/// <summary>
/// Caps free-form operator and diagnostic text at the application boundary so it fits the bounded
/// column it lands in (<c>reason_message</c>, alert message, step error). The single
/// truncator referenced by those entities' column docs; callers pass the column's declared length
/// (typically from <c>ActaSchema</c>) so the limit has one source of truth in the entity.
/// </summary>
internal static class MessageTruncator
{
    /// <summary>
    /// Returns <paramref name="value"/> unchanged when it is null, when <paramref name="maxLength"/>
    /// is null, or when it already fits; otherwise its first <paramref name="maxLength"/> characters.
    /// </summary>
    public static string? Truncate(this string? value, int? maxLength) =>
        value is not null && maxLength is { } max && value.Length > max ? value[..max] : value;
}

/// <summary>
/// Provider-independent product limits for free-form text validated before a store call. Relational
/// schema tests pin these constants to the corresponding column declarations.
/// </summary>
internal static class ActaTextLimits
{
    public const int ActorKey = 128;
    public const int ReasonMessage = 512;
    public const int AlertChannelName = 128;
    public const int AlertDeduplicationKey = 512;
    public const int AlertTitle = 512;
    public const int AlertMessage = 512;
    public const int DefinitionBackoff = 64;
    public const int ScheduleNote = 512;
}
