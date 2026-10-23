namespace Acta;

/// <summary>
/// The operator principal an operator verb stamps into its event's <c>details.actor</c>. The public verbs never
/// take an actor kind from the caller: the system and worker paths write no actor, since the event code and
/// <c>worker_id</c> already say who acted.
/// </summary>
internal static class JobControlActor
{
    /// <summary>
    /// The principal as display text cut to <see cref="ActaTextLimits.Actor"/> with a pair-safe cut, then validated
    /// by <see cref="TextLimits.ValidateDisplayValue"/>; whitespace-only input means "unknown" and comes back null.
    /// </summary>
    public static string? Operator(string? actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            return null;
        }

        var cut = actor.Truncate(ActaTextLimits.Actor)!;
        TextLimits.ValidateDisplayValue(cut, nameof(actor), ActaTextLimits.Actor);
        return cut;
    }
}
