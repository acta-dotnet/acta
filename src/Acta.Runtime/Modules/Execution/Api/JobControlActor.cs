namespace Acta.Modules.Execution.Api;

/// <summary>
/// Who or what caused a control transition, stamped onto the emitted <c>events</c>
/// (<c>actor_code</c> and <c>actor_key</c>). Internal and constructor-validated so callers cannot forge
/// an out-of-range actor or an over-long id; the public <see cref="IJobs"/> control verbs never accept
/// an actor from the caller and stamp <see cref="JobActorCode.Operator"/> themselves.
/// </summary>
internal readonly record struct JobControlActor
{
    private const int MaxActorKeyLength = 128;

    /// <summary>
    /// Build a validated actor. <paramref name="actorKey"/> is ASCII, at most 128 chars, matching the
    /// <c>events.actor_key</c> column.
    /// </summary>
    public JobControlActor(JobActorCode actorCode, string? actorKey = null)
    {
        if (!Enum.IsDefined(actorCode))
        {
            throw new ArgumentOutOfRangeException(nameof(actorCode), actorCode, "Unknown actor code.");
        }

        if (actorKey is not null)
        {
            if (actorKey.Length > MaxActorKeyLength)
            {
                throw new ArgumentException("Actor id cannot exceed 128 characters.", nameof(actorKey));
            }

            foreach (var ch in actorKey)
            {
                if (ch > '\x7f')
                {
                    throw new ArgumentException("Actor id must be ASCII.", nameof(actorKey));
                }
            }
        }

        ActorCode = actorCode;
        ActorKey = actorKey;
    }

    /// <summary>
    /// Fold an externally-sourced operator identity (e.g. an authenticated principal name) into the
    /// column's ASCII contract: non-ASCII characters become '?', over-length input is truncated.
    /// Null stays null. Programmatic callers with known-good ids can construct directly.
    /// </summary>
    public static string? SanitizeActorKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var chars = (value.Length > MaxActorKeyLength ? value[..MaxActorKeyLength] : value).ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] > '\x7f')
            {
                chars[i] = '?';
            }
        }

        return new string(chars);
    }

    /// <summary>Actor classification stamped on the event.</summary>
    public JobActorCode ActorCode { get; }

    /// <summary>Actor identifier, format per <see cref="JobActorCode"/>; may be null.</summary>
    public string? ActorKey { get; }
}
