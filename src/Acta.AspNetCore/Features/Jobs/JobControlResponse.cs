namespace Acta.AspNetCore.Features.Jobs;

/// <summary>
/// HTTP projection of a <see cref="JobControlResult"/> with an operator-readable message. Returned
/// for applied (200), rejected (409), and not-found (404) outcomes alike. Echoes the public
/// <see cref="JobRef"/> from the route; the numeric job id never reaches the wire.
/// </summary>
internal sealed record JobControlResponse(JobRef JobRef, JobControlAction Action, JobStatusCode? Status, string Message);
