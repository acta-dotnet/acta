using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acta.AspNetCore;

/// <summary>
/// Minimal-API endpoints over <see cref="IJobs"/> and <see cref="IActaOperations"/>: GET list and
/// detail reads plus, when enabled, the POST job-control verbs. Query values bind explicitly so
/// malformed input maps to 400, an invalid cursor maps to 400, and a missing job maps to 404.
/// Responses are never cached.
/// </summary>
internal static class ActaApiEndpoints
{
    public static void Map(RouteGroupBuilder group, ActaEndpointOptions options)
    {
        // Names and tags for every endpoint below, derived from its own route. Applied at the group so
        // it reaches the nested controls group too.
        ActaEndpointIdentity.Apply(group);

        // The error model, declared once. Every endpoint under this group can answer these, because
        // the filters below produce them: 400 from the per-endpoint input guards, 413 from the body
        // ceiling, 500 and 503 from the exception backstop. Declaring them at the group is what puts
        // them in the committed contract without restating four lines on 63 endpoints.
        //
        // 415 is deliberately not here. Unlike these four it has no group-level filter: it is
        // produced inside ControlEndpointValidation's four body readers, which only a handler that
        // reads a body calls. Declaring it here would promise it on some forty GET reads that cannot
        // execute that code, so each body-reading route declares it where its body is read.
        group.ProducesProblem(StatusCodes.Status400BadRequest);
        group.ProducesProblem(StatusCodes.Status413PayloadTooLarge);
        group.ProducesProblem(StatusCodes.Status500InternalServerError);
        group.ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // Responses are declared; request bodies are not, and deliberately. Accepts<T> writes an
        // accepted-content-type onto the endpoint, and routing then refuses a request that does not
        // carry it - which turned every optional body here into a 404, because these endpoints read
        // their bodies from HttpContext and treat them as optional. Six endpoint tests caught it.
        // Documenting a schema is not worth a routing regression, so the request shapes live in the
        // per-endpoint comments and in docs/ until the framework offers schema-only metadata.

        // Never surface a raw 500/stack from the dashboard API: any unhandled exception (e.g. the database
        // is unreachable) becomes a 503 ProblemDetails the frontend shows as a banner and retries. Known
        // input errors are still mapped to 400/404 by the per-endpoint Guard before they reach this backstop.
        group.AddEndpointFilter(
            static async (context, next) =>
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
                catch (PayloadTooLargeException ex)
                {
                    // The BoundedReadStream ceiling tripped mid-read (a chunked body that outran its
                    // declared length). Same type the ledger throws for an oversized payload.
                    return Results.Problem(
                        statusCode: StatusCodes.Status413PayloadTooLarge,
                        title: "Request body too large.",
                        detail: ex.Message
                    );
                }
                catch (BadHttpRequestException ex)
                {
                    // The server itself rejected the request (malformed framing, bad chunked encoding).
                    return Results.Problem(statusCode: ex.StatusCode, title: "Invalid request.", detail: ex.Message);
                }
                catch (Exception ex)
                {
                    var loggerFactory = context.HttpContext.RequestServices.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
                    loggerFactory?.CreateLogger("Acta.AspNetCore").LogError(ex, "Unhandled Acta API exception.");

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
        );

        group.AddEndpointFilter(
            static async (context, next) =>
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                return await next(context);
            }
        );

        // Aggregate request-body ceiling for every mapped endpoint. Declared lengths reject up front;
        // the bounded stream keeps the ceiling enforced for chunked bodies, surfacing an overrun as the
        // 413 BadHttpRequestException the error backstop above translates.
        group.AddEndpointFilter(
            async (context, next) =>
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
        );

        // Null when controls are off; each group's route table maps its writes onto it only when it is there.
        RouteGroupBuilder? controls = null;
        if (options.EnableControls)
        {
            // A nested group (empty prefix, same routes) rather than mapping straight onto `group`: it
            // gives the authorization filter below a scope that covers every control endpoint and no
            // read endpoint, without touching the endpoint families' own Map methods.
            controls = group.MapGroup("");
            ControlAuthorizationFilter.Attach(controls);

            // Controls add one code the reads cannot answer: 403, when the host's own authorizer
            // denies the request. The per-family 404 and 409 are declared where their bodies are.
            controls.ProducesProblem(StatusCodes.Status403Forbidden);
        }

        JobsEndpoints.Map(group, controls, options);
        WorkersEndpoints.Map(group, controls, options);
        NamespacesEndpoints.Map(group, controls, options);
        TenantsEndpoints.Map(group, controls, options);
        AlertsEndpoints.Map(group, controls, options);
        OutboxEndpoints.Map(group, controls, options);
        DefinitionsEndpoints.Map(group, controls, options);
        SchedulesEndpoints.Map(group, controls, options);
        LedgerEndpoints.Map(group, options);

        group
            .MapGet(
                "/capabilities",
                (HttpContext http, IActaOperations operations) =>
                {
                    var provider = operations.Provider switch
                    {
                        DbProvider.SqlServer => "mssql",
                        DbProvider.Postgres => "pg",
                        DbProvider.Sqlite => "sqlite",
                        _ => "unknown",
                    };
                    var version =
                        typeof(IJobs).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                        ?? typeof(IJobs).Assembly.GetName().Version?.ToString()
                        ?? "unknown";
                    // The configured Acta schema qualifies the operator views the dashboard's Copy-SQL emits.
                    // Resolved optionally so a host that maps the API without a registered provider still answers
                    // (the provider package registers SqlProviderOptions); the default matches the option default.
                    var schema = http.RequestServices.GetService<SqlProviderOptions>()?.Schema ?? "acta";
                    var body = new CapabilitiesResponse(
                        ControlsEnabled: options.EnableControls,
                        Version: version,
                        Provider: provider,
                        Schema: schema,
                        ConfirmationHeader: options.ControlConfirmationHeaderName
                    );
                    return Results.Json(body, DashboardJsonContext.Default.CapabilitiesResponse);
                }
            )
            .WithSummary("Read this host's API capabilities.")
            .Produces<CapabilitiesResponse>(StatusCodes.Status200OK);
    }
}
