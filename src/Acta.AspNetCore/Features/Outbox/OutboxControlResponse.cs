namespace Acta.AspNetCore.Features.Outbox;

/// <summary>
/// HTTP projection of an <see cref="OutboxControlResult"/> with an operator-readable message.
/// Returned for accepted (202), rejected (409), and not-found (404) outcomes alike;
/// <c>PendingSinceUtc</c> is set only on a rejection and carries the blocking command's park instant.
/// </summary>
internal sealed record OutboxControlResponse(ControlAction Action, DateTime? PendingSinceUtc, string Message);
