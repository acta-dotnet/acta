namespace Acta;

/// <summary>
/// Who or what caused a control transition, stamped onto the emitted <c>events</c>
/// (<c>actor_code</c> and <c>actor</c>). Internal and constructor-validated so callers cannot forge
/// an out-of-range actor code or store text the column refuses; the public <see cref="IJobs"/> control verbs never
/// accept an actor code from the caller and stamp <see cref="ActorCode.Operator"/> themselves.
/// </summary>
internal readonly record struct JobControlActor
{
    /// <summary>
    /// Build a validated actor. <paramref name="actor"/> is display text: whitespace-only input means "unknown" and
    /// stores as null, and anything else passes <see cref="TextLimits.ValidateDisplayValue"/> against the
    /// <see cref="ActaTextLimits.Actor"/> width of the <c>events.actor</c> column. Externally-sourced identities
    /// (an authenticated principal name) are cut to the column by <see cref="Operator"/> first.
    /// </summary>
    public JobControlActor(ActorCode actorCode, string? actor = null)
    {
        if (!ActorCode.IsWritableId((byte)actorCode))
        {
            throw new ArgumentOutOfRangeException(nameof(actorCode), actorCode, "Unknown actor code.");
        }

        if (string.IsNullOrWhiteSpace(actor))
        {
            actor = null;
        }
        else
        {
            TextLimits.ValidateDisplayValue(actor, nameof(actor), ActaTextLimits.Actor);
        }

        ActorCode = actorCode;
        Actor = actor;
    }

    /// <summary>The operator behind a public control verb: the name is cut to the column with a pair-safe cut, then
    /// validated.</summary>
    public static JobControlActor Operator(string? actor) => new(ActorCode.Operator, actor.Truncate(ActaTextLimits.Actor));

    /// <summary>Actor classification stamped on the event.</summary>
    public ActorCode ActorCode { get; }

    /// <summary>Who acted, as display text whose format follows <see cref="ActorCode"/>; may be null.</summary>
    public string? Actor { get; }
}
