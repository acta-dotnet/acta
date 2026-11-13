using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.AspNetCore;

/// <summary>
/// GET /capabilities: answers <see cref="GetCapabilitiesResponse"/> from this host's options and provider. It takes no
/// input and reads no database, so it answers no 400 and no 503.
/// </summary>
internal static class GetCapabilitiesEndpoint
{
    public static void Map(RouteGroupBuilder reads, ActaEndpointOptions options) =>
        reads
            .MapGet("/capabilities", async (HttpContext http, IActaOperations operations) =>
            {
                var version = typeof(IJobs).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? typeof(IJobs).Assembly.GetName().Version?.ToString()
                    ?? "unknown";
                // The configured Acta schema qualifies the operator views the dashboard's Copy-SQL emits.
                // Resolved optionally so a host that maps the API without a registered provider still answers
                // (the provider package registers SqlProviderOptions); the default matches the option default.
                var schema = http.RequestServices.GetService<SqlProviderOptions>()?.Schema ?? "acta";
                var body = new GetCapabilitiesResponse(
                    ControlsEnabled: options.EnableControls,
                    EngineVersion: version,
                    Provider: ProviderCode(operations.Provider),
                    Schema: schema,
                    ConfirmationHeader: options.ControlConfirmationHeaderName,
                    // Without RequirePermissions no policy exists to ask, and every route serves whoever reaches it.
                    Permissions: options.RequirePermissions ? await PermissionFilter.HeldAsync(http) : PermissionFilter.Permissions
                );
                return Results.Json(body, ActaApiJsonContext.Relaxed.GetCapabilitiesResponse);
            })
            .WithMetadata(new ActaOperationAttribute("getCapabilities", ActaPermissions.Read))
            .WithSummary("Read this host's API capabilities.")
            .Produces<GetCapabilitiesResponse>(StatusCodes.Status200OK);

    /// <summary>The provider's code on the wire: the dialect token its SQL files carry.</summary>
    internal static string ProviderCode(DbProvider provider) =>
        provider switch
        {
            DbProvider.SqlServer => "mssql",
            DbProvider.Postgres => "pg",
            DbProvider.Sqlite => "sqlite",
            _ => "unknown",
        };
}
