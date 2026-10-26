using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acta.AspNetCore;

/// <summary>The filters every API route runs behind, outermost first: the error backstop, no-store, and the body ceiling.</summary>
internal static class ApiFilters
{
    /// <summary>
    /// Never surface a raw 500 or stack from the API: an unhandled exception becomes a problem document the dashboard
    /// shows as a banner and retries. Known input errors are mapped to 400 and 404 by the routes before they get here.
    /// </summary>
    public static async ValueTask<object?> ErrorBackstop(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected or the host is shutting down mid-request: not a fault, no log,
            // and do not rethrow into developer exception middleware. A connected test transport
            // can observe 499; a genuinely disconnected client has no response channel left.
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (EnqueueRejectedException ex)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Enqueue rejected.",
                detail: ex.Message,
                extensions: new Dictionary<string, object?> { ["reasonCode"] = ex.Reason.ToString() }
            );
        }
        catch (JobPurgeBlockedException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Purge blocked.", detail: ex.Message);
        }
        catch (PayloadTooLargeException ex)
        {
            // The BoundedReadStream ceiling tripped mid-read (a chunked body that outran its
            // declared length). Same type the ledger throws for an oversized payload.
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Request body too large.", detail: ex.Message);
        }
        catch (BadHttpRequestException ex)
        {
            // The server itself rejected the request (malformed framing, bad chunked encoding).
            return Results.Problem(statusCode: ex.StatusCode, title: "Invalid request.", detail: ex.Message);
        }
        catch (Exception ex)
        {
            if (context.HttpContext.RequestServices.GetService(typeof(ILoggerFactory)) is ILoggerFactory loggerFactory)
            {
                ActaAspNetCoreLog.UnhandledApiException(loggerFactory.CreateLogger("Acta.AspNetCore"), ex);
            }

            // Classify without echoing exception text: driver messages can carry connection
            // strings. A fixed reason keyed off the exception family stays safe and still tells
            // a local developer what actually broke; the full detail is in the host log above.
            // Only the known-transient family (database/network/timeout, including a provider
            // command timeout surfacing as a non-abort cancellation) reports 503 retry-later;
            // anything else is a server fault and reports 500 so it is never mistaken for a
            // recoverable outage.
            var transientFailure = false;
            for (var cause = ex; cause is not null; cause = cause.InnerException)
            {
                if (
                    cause
                    is System.Data.Common.DbException
                        or System.Net.Sockets.SocketException
                        or TimeoutException
                        or OperationCanceledException
                )
                {
                    transientFailure = true;
                    break;
                }
            }

            return transientFailure
                ? Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Service unavailable.",
                    detail: "The Acta API is temporarily unavailable: the database is unreachable or rejected the request."
                )
                : Results.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Internal server error.",
                    detail: "The Acta API failed to process the request; see the host log for detail."
                );
        }
    }

    public static async ValueTask<object?> NoStore(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        return await next(context);
    }

    /// <summary>
    /// The aggregate request-body ceiling. A declared length past it is refused up front; the bounded stream keeps it
    /// enforced for chunked bodies, surfacing an overrun as the 413 the error backstop translates.
    /// </summary>
    public static async ValueTask<object?> BodyCeiling(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;

        // The one payload ceiling, read from the runtime options. A body the ledger would
        // refuse is refused here, with the same number rather than a second one.
        var cap = context.HttpContext.RequestServices.GetRequiredService<IOptions<JobsOptions>>().Value.MaxInlinePayloadBytes;

        if (request.ContentLength is { } declared && declared > cap)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "Request body too large.",
                detail: $"Request bodies on this endpoint are capped at {cap} bytes."
            );
        }

        request.Body = new BoundedReadStream(request.Body, cap);
        return await next(context);
    }
}
