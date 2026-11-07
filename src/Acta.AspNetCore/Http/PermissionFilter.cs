using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.AspNetCore;

/// <summary>
/// The check behind <see cref="ActaEndpointOptions.RequirePermissions"/>: each request is authorized against the host's
/// policy named for its operation's permission. Acta evaluates the policy in its own endpoint filter, so metadata the
/// routes inherit from the host, an <c>AllowAnonymous</c> included, cannot switch the check off.
/// </summary>
internal static class PermissionFilter
{
    private static readonly string[] Permissions =
    [
        ActaPermissions.Read,
        ActaPermissions.Enqueue,
        ActaPermissions.Control,
        ActaPermissions.Admin,
    ];

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
        var policy =
            await http.RequestServices.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(permission)
            ?? throw new InvalidOperationException($"The authorization policy '{permission}' is not defined.");

        var evaluator = http.RequestServices.GetRequiredService<IPolicyEvaluator>();
        var authentication = await evaluator.AuthenticateAsync(policy, http);
        var authorization = await evaluator.AuthorizeAsync(policy, authentication, http, http);
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

        // No credential. An API route answers a plain 401, so a cookie scheme never redirects a client to a login page;
        // a page is challenged by the host's scheme, which is where a browser should be sent to sign in.
        if (operation is not null)
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        if (policy.AuthenticationSchemes.Count == 0)
        {
            await http.ChallengeAsync();
        }

        foreach (var scheme in policy.AuthenticationSchemes)
        {
            await http.ChallengeAsync(scheme);
        }

        return Results.Empty;
    }
}
