namespace Acta;

/// <summary>
/// What a job control transition writes onto its event: the operator principal (null on a system path), the
/// <see cref="EventReason"/> when the code does not imply it, and the human message. A null
/// <c>ExpectedVersion</c> writes unconditionally.
/// </summary>
internal sealed record JobControlInput(string? Actor, string? Reason, string? Message, int? ExpectedVersion = null)
{
    /// <summary>The event's JSON details: the actor and the reason, or null when it has neither.</summary>
    public byte[]? Details => EventDetails.Write(("actor", Actor), ("reason", Reason));
}
