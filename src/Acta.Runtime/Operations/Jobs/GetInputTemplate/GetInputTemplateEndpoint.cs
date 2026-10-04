using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// HTTP projection of a <see cref="JobInputTemplate"/>. <c>Template</c> is the skeleton inlined as raw
/// JSON (never a string); a job this host does not know reports every field null with format "none".
/// </summary>
internal sealed record GetInputTemplateHttpResponse(
    string JobNamespace,
    string JobName,
    string? InputTypeName,
    string InputFormatName,
    JsonElement? Template
);

/// <summary>
/// GET /jobs/input-template: the enqueue form's shape hint, served from the in-process manifest.
/// A job this host never registered is not an error (a dashboard can point at a shared ledger it
/// has no job assembly for), so it answers 200 with a null template and the form seeds `{}`.
/// </summary>
internal static class GetInputTemplateEndpoint
{
    public static void Map(RouteGroupBuilder reads) =>
        reads
            .MapGet(
                "/jobs/input-template",
                IResult (string? jobNamespace, string? jobName, IJobs jobs) =>
                {
                    // A required query parameter that did not arrive is caller input, not a miss - the
                    // same answer /jobs/by-key gives its own two required parameters.
                    if (string.IsNullOrWhiteSpace(jobNamespace) || string.IsNullOrWhiteSpace(jobName))
                    {
                        return ControlEndpointValidation.Problem(
                            StatusCodes.Status400BadRequest,
                            "Invalid request.",
                            "jobNamespace and jobName are required."
                        );
                    }

                    var template = jobs.GetInputTemplate(jobNamespace, jobName);
                    JsonElement? shape = null;
                    if (template?.TemplateJson is { } json)
                    {
                        // Cloned out of the document so the element outlives the using scope.
                        using var document = JsonDocument.Parse(json);
                        shape = document.RootElement.Clone();
                    }

                    return Results.Json(
                        new GetInputTemplateHttpResponse(
                            jobNamespace,
                            jobName,
                            template?.InputTypeName,
                            template?.InputFormat.Name ?? JobPayloadFormat.None.Name,
                            shape
                        ),
                        DashboardJsonContext.Default.GetInputTemplateHttpResponse
                    );
                }
            )
            .WithSummary("Read a job's compile-time input template.")
            // No 404: a name this host has no assembly for is a 200 with a null template, and the only
            // other answer is the group-wide 400 for a missing required parameter.
            .Produces<GetInputTemplateHttpResponse>(StatusCodes.Status200OK);
}
