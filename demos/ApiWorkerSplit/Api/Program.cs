using Acta;
using Acta.Demos.ApiWorkerSplit;
using Acta.Demos.ApiWorkerSplit.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.UseActa(j => j.UseLocalDatabase(builder.Configuration));

var app = builder.Build();

// A raw JobEnqueueRequest names the route (namespace + job name) explicitly, how an enqueue-only host
// reaches a job without referencing the worker's manifest.
app.MapPost(
    "/welcome-emails",
    async (SendWelcomeEmail input, HttpContext http, IJobs jobs, CancellationToken ct) =>
    {
        var deduplicationKey = $"welcome:{input.UserId}";
        var correlationKey = http.TraceIdentifier;

        var request = new JobEnqueueRequest(
            WelcomeEmailRoute.Namespace,
            WelcomeEmailRoute.JobName,
            JobPayload.Json(input),
            DeduplicationKey: deduplicationKey,
            CorrelationKey: correlationKey,
            Tags: [new TagInput("kind", "email"), new TagInput("tenant", "demo")]
        );

        var outcome = await jobs.EnqueueAsync(request, ct);
        var statusUrl = $"/welcome-emails/{outcome.JobRef}";

        return Results.Accepted(statusUrl, new WelcomeEmailAccepted(outcome.JobRef, outcome.Action.ToString(), statusUrl, correlationKey));
    }
);

app.MapGet(
    "/welcome-emails/{jobRef}",
    async (string jobRef, IJobs jobs, CancellationToken ct) =>
    {
        if (!JobRef.TryParse(jobRef, out var parsed))
        {
            return Results.BadRequest(new { error = "Invalid job ref." });
        }

        var snapshot = await jobs.GetAsync(parsed, ct);
        return snapshot is null
            ? Results.NotFound()
            : Results.Ok(
                new WelcomeEmailStatus(
                    snapshot.JobRef,
                    snapshot.Status.ToString(),
                    snapshot.DeduplicationKey,
                    snapshot.CreatedAtUtc,
                    snapshot.ModifiedAtUtc
                )
            );
    }
);

Console.WriteLine("API process: enqueue-only, no worker handlers loaded.");
Console.WriteLine("Run the worker in another terminal:");
Console.WriteLine("  dotnet run --project demos/ApiWorkerSplit/Worker");

await app.RunAsync();
