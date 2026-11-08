using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Acta.AspNetCore;

/// <summary>
/// POST /jobs/{jobRef}/signals/{signalName}: raise a named signal, a control like the other verbs. The name
/// is folded to lowercase and must be a name outside the reserved <c>sys</c> names, which keeps out the child latches
/// (<c>sys.child.{id}</c>). An empty body raises a presence-only signal; a JSON body is stored verbatim for the handler's
/// WaitSignalAsync&lt;T&gt; to deserialize.
/// </summary>
internal static class RaiseJobSignalEndpoint
{
    public static void Map(RouteGroupBuilder controls) =>
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

                    // An empty body raises a presence-only signal; a body that is one strict JSON value passes through verbatim.
                    JobPayload payload;
                    try
                    {
                        payload = bytes is null ? JobPayload.None : JobPayload.FromBytes(JobPayloadFormat.Json, bytes);
                    }
                    catch (ArgumentException ex)
                    {
                        return ControlEndpointValidation.Problem(StatusCodes.Status400BadRequest, "Invalid signal value.", ex.Message);
                    }

                    // Operator identity for the audit trail comes from the authenticated principal, never the
                    // body; the verb stamps actor = Operator.
                    var actor = http.User?.Identity?.Name;

                    try
                    {
                        var result = await jobs.RaiseSignalAsync(JobLookup.ByRef(parsed), signalName, payload, actor, ct);
                        return RouteTable.Control(result, ControlStatus.Job<RaiseJobSignalResponse>());
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
            .WithMetadata(new ActaOperationAttribute("raiseJobSignal", ActaPermissions.Control))
            .WithSummary("Raise a named signal on the job, with an optional JSON value.")
            // The signal value is free-form JSON passed through verbatim, so the declared body type is
            // "any JSON" rather than a record; optional because an empty body raises a presence-only signal.
            .AcceptsJson<System.Text.Json.JsonElement>(optional: true)
            .Produces<RaiseJobSignalResponse>(StatusCodes.Status200OK)
            .Produces<RaiseJobSignalResponse>(StatusCodes.Status409Conflict)
            .Produces<RaiseJobSignalResponse>(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
}
