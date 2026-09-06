using Acta.Runtime.Kernel;

namespace Acta.Runtime.Modules.Execution.Api;

/// <summary>
/// Who or what caused a control transition, stamped onto the emitted <c>events</c>
/// (<c>actor_code</c> and <c>actor_key</c>). Internal and constructor-validated so callers cannot forge
/// an out-of-range actor or an over-long id; the public <see cref="IJobs"/> control verbs never accept
/// an actor from the caller and stamp <see cref="ActorCode.Operator"/> themselves.
/// </summary>
internal readonly record struct JobControlActor
{
    /// <summary>
    /// Build a validated actor. <paramref name="actorKey"/> is at most <see cref="ActaTextLimits.ActorKey"/>
    /// chars, matching the <c>events.actor_key</c> column; whitespace-only input means "unknown" and stores as null.
    /// Externally-sourced identities (an authenticated principal name) are cut to the column through
    /// <c>MessageTruncator</c> by the caller, so a pair-safe cut is the only transformation they see.
    /// </summary>
    public JobControlActor(ActorCode actorCode, string? actorKey = null)
    {
        if (!Enum.IsDefined(actorCode))
        {
            throw new ArgumentOutOfRangeException(nameof(actorCode), actorCode, "Unknown actor code.");
        }

        if (actorKey is { Length: > ActaTextLimits.ActorKey })
        {
            throw new ArgumentException($"Actor key cannot exceed {ActaTextLimits.ActorKey} characters.", nameof(actorKey));
        }

        ActorCode = actorCode;
        ActorKey = string.IsNullOrWhiteSpace(actorKey) ? null : actorKey;
    }

    /// <summary>Actor classification stamped on the event.</summary>
    public ActorCode ActorCode { get; }

    /// <summary>Actor identifier, format per <see cref="ActorCode"/>; may be null.</summary>
    public string? ActorKey { get; }
}
