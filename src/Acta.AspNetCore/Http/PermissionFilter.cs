using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Acta.AspNetCore;

/// <summary>
/// The check behind <see cref="ActaEndpointOptions.RequirePermissions"/>: each request is authorized against the host's
/// policy named for its operation's permission. Acta evaluates the policy in its own endpoint filter, so metadata the
/// routes inherit from the host, an <c>AllowAnonymous</c> included, cannot switch the check off.
/// </summary>
internal static class PermissionFilter
{
    internal static readonly string[] Permissions =
    [
        ActaPermissions.Read,
        ActaPermissions.Enqueue,
        ActaPermissions.Control,
        ActaPermissions.Admin,
    ];

    // The principal the request carried before the check authenticated it for its route, kept for the capabilities read.
    private static readonly object CallerKey = new();

    /// <summary>Attaches the check to the mount; throws unless the host defines a policy for every permission.</summary>
    public static void Attach(RouteGroupBuilder group, IServiceProvider services)
    {
        // The evaluator is asked for, not built: building it builds the host's authorization handlers, and a scoped one
        // cannot come from the root provider.
        if (
            services.GetService<IAuthorizationPolicyProvider>() is not { } provider
            || services.GetService<IServiceProviderIsService>()?.IsService(typeof(IPolicyEvaluator)) != true
        )
        {
            throw new InvalidOperationException(
                "RequirePermissions needs the host's authorization services: call AddAuthorization and define a policy for each of "
                    + $"{string.Join(", ", Permissions)}."
            );
        }

        foreach (var permission in Permissions)
        {
            // Mapping is synchronous, and a policy provider answers from memory.
            if (provider.GetPolicyAsync(permission).GetAwaiter().GetResult() is null)
            {
                throw new InvalidOperationException(
                    $"RequirePermissions needs an authorization policy named '{permission}'; define one for each of "
                        + $"{string.Join(", ", Permissions)}."
                );
            }
        }

        group.AddEndpointFilter(CheckAsync);
    }

    private static async ValueTask<object?> CheckAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        // A route of the API names its permission; what else the mount serves is the dashboard's pages, which read.
        var operation = http.GetEndpoint()?.Metadata.GetMetadata<ActaOperationAttribute>();
        var permission = operation?.Permission ?? ActaPermissions.Read;
        http.Items[CallerKey] = http.User;
        var (policy, authorization) = await EvaluateAsync(http, permission);
        if (authorization.Succeeded)
        {
            return await next(context);
        }

        if (authorization.Forbidden)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Permission denied.",
                detail: $"This operation needs the {permission} permission."
            );
        }

        // No credential the policy accepts. The host's scheme writes the challenge, so a client that authenticates in a
        // second step, or whose token has expired, reads from WWW-Authenticate what to send.
        var schemes = policy.AuthenticationSchemes;
        if (
            schemes.Count == 0
            && http.RequestServices.GetService<IAuthenticationSchemeProvider>() is { } provider
            && await provider.GetDefaultChallengeSchemeAsync() is { } fallback
        )
        {
            schemes = [fallback.Name];
        }

        foreach (var scheme in schemes)
        {
            await http.ChallengeAsync(scheme);
        }

        // A scheme that sends a browser to a login page does so only for a dashboard page. An API route answers 401
        // whatever status the challenge set, without its Location, and so does a call the catch-all would answer as an
        // unknown endpoint, such as a control this mount does not map; with no scheme to challenge, the 401 is all there
        // is to answer.
        var api =
            operation is not null
            || http.GetRouteValue(ActaEndpointRouteBuilderExtensions.CatchAllRouteValue) is string path
                && ActaEndpointRouteBuilderExtensions.IsUnknownEndpoint(http.Request, path);
        var response = http.Response;
        if ((api || schemes.Count == 0) && !response.HasStarted)
        {
            response.Headers.Remove(HeaderNames.Location);
            response.StatusCode = StatusCodes.Status401Unauthorized;
        }

        return Results.Empty;
    }

    /// <summary>
    /// The permissions whose policy this request's caller satisfies, in declared order, each probed from the principal
    /// the request carried before its route's check: a policy that names its own schemes replaces the principal, so a
    /// probe that started from another probe's would answer for a different caller. <c>HttpContext.User</c> is put back
    /// after each probe.
    /// </summary>
    public static async Task<IReadOnlyList<string>> HeldAsync(HttpContext http)
    {
        var current = http.User;
        var caller = http.Items[CallerKey] as System.Security.Claims.ClaimsPrincipal ?? current;
        var held = new List<string>();
        foreach (var permission in Permissions)
        {
            http.User = caller;
            try
            {
                if ((await EvaluateAsync(http, permission)).Authorization.Succeeded)
                {
                    held.Add(permission);
                }
            }
            // A policy whose scheme fails to run is not held: its own routes answer that failure, and the other
            // permissions still answer for the dashboard's gates.
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Not held.
            }
            finally
            {
                http.User = current;
            }
        }

        return held;
    }

    private static async Task<(AuthorizationPolicy Policy, PolicyAuthorizationResult Authorization)> EvaluateAsync(
        HttpContext http,
        string permission
    )
    {
        var policy =
            await http.RequestServices.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(permission)
            ?? throw new InvalidOperationException($"The authorization policy '{permission}' is not defined.");
        var evaluator = http.RequestServices.GetRequiredService<IPolicyEvaluator>();
        var authentication = await evaluator.AuthenticateAsync(policy, http);
        return (policy, await evaluator.AuthorizeAsync(policy, authentication, http, http));
    }
}
