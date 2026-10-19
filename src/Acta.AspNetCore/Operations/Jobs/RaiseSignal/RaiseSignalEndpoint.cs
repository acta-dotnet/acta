using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Acta.AspNetCore;

/// <summary>
/// POST /jobs/{jobRef}/signals/{signalName}: raise a named signal, behind EnableControls and the confirmation header
/// like the other verbs. The name is folded to lowercase and must be a name outside the reserved <c>sys</c> names, which
/// keeps out the child latches (<c>sys.child.{id}</c>). An empty body raises a presence-only signal; a JSON body is stored verbatim for
/// the handler's WaitSignalAsync&lt;T&gt; to deserialize.
/// </summary>
internal static class RaiseSignalEndpoint
{
    public static void Map(RouteGroupBuilder controls, ActaEndpointOptions options) =>
        controls
            .MapPost(
                "/jobs/{jobRef}/signals/{signalName}",
                async (
                    string jobRef,
                    string signalName,
                    HttpContext http,
                    IJobs jobs,
                    IOptions<JobsOptions> jobsOptions,
                    CancellationToken ct
                ) =>
                {
                    if (!JobRef.TryParse(jobRef, out var parsed))
                    {
                        return RefSegment.Malformed("jobRef", "job");
                    }

                    if (ControlEndpointValidation.CheckConfirmation(http, options) is { } confirmationError)
                    {
                        return confirmationError;
                    }

                    try
                    {
                        signalName = NameSyntax.Canonicalize(
                            signalName,
                            nameof(signalName),
                            NameSyntax.ExtendedMaxLength,
                            reserveSystem: true
                        );
                    }
                    catch (ArgumentException ex)
                    {
                        return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid signal name.", ex.Message);
                    }

                    var (bytes, error) = await ControlEndpointValidation.ReadOptionalJsonBytesAsync(
                        http,
                        jobsOptions.Value.MaxInlinePayloadBytes,
                        ct
                    );
                    if (error is not null)
                    {
                        return error;
                    }

                    // An empty body raises a presence-only signal; a JSON body passes through verbatim.
                    JobPayload payload;
                    try
                    {
                        payload = bytes is null ? JobPayload.None : JobPayload.FromBytes(JobPayloadFormat.Json, bytes);
                    }
                    catch (ArgumentException)
                    {
                        return ControlEndpointValidation.Problem(
                            StatusCodes.Status400BadRequest,
                            "Invalid signal value.",
                            "The body must be UTF-8 with no NUL (0x00) byte."
                        );
                    }

                    // Operator identity for the audit trail comes from the authenticated principal, never the
                    // body; the verb stamps actor = Operator.
                    var actor = http.User?.Identity?.Name;

                    try
                    {
                        var result = await jobs.RaiseSignalAsync(JobLookup.ByRef(parsed), signalName, payload, actor, ct);
                        return RouteTable.Control(result, ControlStatus.Job<RaiseSignalResponse>());
                    }
                    catch (PayloadTooLargeException ex)
                    {
                        return ControlEndpointValidation.Problem(
                            StatusCodes.Status413PayloadTooLarge,
                            "Signal value too large.",
                            ex.Message
                        );
                    }
                }
            )
            .WithSummary("Raise a named signal on the job, with an optional JSON value.")
            // The signal value is free-form JSON passed through verbatim, so the declared body type is
            // "any JSON" rather than a record; optional because an empty body raises a presence-only signal.
            .AcceptsJson<System.Text.Json.JsonElement>(optional: true)
            .Produces<RaiseSignalResponse>(StatusCodes.Status200OK)
            .Produces<RaiseSignalResponse>(StatusCodes.Status409Conflict)
            .Produces<RaiseSignalResponse>(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);
}
