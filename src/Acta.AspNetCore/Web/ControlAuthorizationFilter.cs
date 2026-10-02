using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.AspNetCore.Web;

/// <summary>
/// Attaches the optional <see cref="IActaControlAuthorizer"/> seam to the control-only route group. Not
/// registered in DI: this is a no-op and every control request proceeds exactly as before. Registered:
/// every control request is authorized before its handler runs; a denial short-circuits to 403 and the
/// handler (so the store call and its audit event) never runs.
/// </summary>
internal static class ControlAuthorizationFilter
{
    /// <summary>
    /// The entity literals every control endpoint family routes under. A route's combined pattern
    /// (host mount prefix + local path) is scanned for the last of these; whatever mount prefix a host
    /// chooses, the control routes always end in one of these, so this stays stable regardless of where
    /// the API is mounted. Extend this list alongside a new control-endpoint family.
    /// </summary>
    private static readonly string[] Entities =
    [
        "jobs",
        "tenants",
        "namespaces",
        "schedules",
        "definitions",
        "alerts",
        "workers",
        "outbox",
    ];

    public static void Attach(RouteGroupBuilder controls)
    {
        controls.AddEndpointFilter(
            async (context, next) =>
            {
                var http = context.HttpContext;
                var authorizer = http.RequestServices.GetService<IActaControlAuthorizer>();
                if (authorizer is null)
                {
                    return await next(context);
                }

                var pattern = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "";
                var request = new ActaControlRequest(DeriveVerb(pattern, http.Request.Method), http, http.User?.Identity?.Name);
                var decision = await authorizer.AuthorizeAsync(request, http.RequestAborted);
                return decision.IsAllowed
                    ? await next(context)
                    : Results.Problem(
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "Control request denied.",
                        detail: decision.Reason
                    );
            }
        );
    }

    /// <summary>
    /// "jobs" is dropped as the implicit default entity (/jobs/{jobRef}/cancel -&gt; "cancel"); other
    /// families keep their entity prefix (/tenants/{key}/suspend -&gt; "tenants.suspend"). Route parameters
    /// are skipped, and where one route carries two mutations the method names which: a tag write is
    /// ".add" or ".remove", a PATCH is ".update", and a POST to a bare entity ".register". A pattern with
    /// no recognized entity or no literal segments left falls back to the HTTP method.
    /// </summary>
    internal static string DeriveVerb(string pattern, string method)
    {
        var allSegments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var entityIndex = Array.FindLastIndex(allSegments, s => Array.IndexOf(Entities, s) >= 0);
        var segments = (entityIndex < 0 ? allSegments : allSegments[entityIndex..]).Where(s => s[0] != '{').ToArray();

        // POST /jobs is the sole control that reduces to the bare "jobs" entity; name it for what it does.
        if (segments is ["jobs"] && HttpMethods.IsPost(method))
        {
            return "enqueue";
        }

        if (segments.Length > 1 && segments[0] == "jobs")
        {
            segments = segments[1..];
        }

        var suffix =
            HttpMethods.IsDelete(method) ? "remove"
            : HttpMethods.IsPatch(method) ? "update"
            : !HttpMethods.IsPost(method) ? null
            : segments is [.., "tags"] ? "add"
            : segments is [var only] && Array.IndexOf(Entities, only) >= 0 ? "register"
            : null;
        if (suffix is not null)
        {
            segments = [.. segments, suffix];
        }

        return segments.Length == 0 ? method.ToLowerInvariant() : string.Join('.', segments);
    }
}
