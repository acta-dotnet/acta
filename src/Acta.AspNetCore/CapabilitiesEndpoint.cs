using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.AspNetCore;

/// <summary>
/// Minimal read-side capabilities contract the dashboard consumes to show/hide edit UI. Additive.
/// </summary>
internal sealed record CapabilitiesResponse(
    bool ControlsEnabled,
    string Version,
    string Provider,
    string Schema,
    string ConfirmationHeader
);

/// <summary>GET /capabilities: what this host's API allows, so the dashboard offers only the controls it can use.</summary>
internal static class CapabilitiesEndpoint
{
    public static void Map(RouteGroupBuilder reads, ActaEndpointOptions options) =>
        reads
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
