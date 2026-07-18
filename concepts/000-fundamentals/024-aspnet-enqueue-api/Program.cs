using Acta;
using Acta.Concepts.AspNetEnqueueApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<AspNetEnqueueApiJobs>("aspnet-enqueue-api");
});

var app = builder.Build();

// POST writes a durable job and returns 202; the import runs on the worker after the request ends.
// DeduplicationKey dedupes a retry with the same tenant/import id onto the same job.
app.MapPost(
    "/imports",
    async (StartImportRequest request, HttpContext http, IJobs jobs, CancellationToken ct) =>
    {
        var businessKey = $"import:{request.Tenant}:{request.ImportId}";
        var deduplicationKey = DeduplicationKey.ForDefinition("import-csv", businessKey);
        var correlationKey = http.TraceIdentifier;

        var outcome = await jobs.EnqueueAsync(
            new ImportCsv(request.Tenant, request.ImportId, request.FileName),
            o => o.DeduplicationKey(deduplicationKey).CorrelationKey(correlationKey).Tag("tenant", request.Tenant).Tag("kind", "csv"),
            ct
        );

        var statusUrl = $"/imports/{outcome.JobRef}";

        return Results.Accepted(statusUrl, new StartImportResponse(outcome.JobRef, outcome.Action.ToString(), statusUrl, correlationKey));
    }
);

// GET polls the public JobRef; the result is read back only once the job is terminal.
app.MapGet(
    "/imports/{jobRef}",
    async (string jobRef, IJobs jobs, CancellationToken ct) =>
    {
        if (!JobRef.TryParse(jobRef, out var parsed))
        {
            return Results.BadRequest(new { error = "Invalid job ref." });
        }

        var snapshot = await jobs.GetAsync(parsed, ct);
        if (snapshot is null)
        {
            return Results.NotFound();
        }

        ImportResult? result = null;
        if (snapshot.Status.IsTerminal)
        {
            result = await jobs.GetResultAsync<ImportResult>(parsed, ct);
        }

        return Results.Ok(
            new ImportStatusResponse(
                snapshot.JobRef,
                snapshot.Status.ToString(),
                snapshot.DeduplicationKey,
                snapshot.CreatedAtUtc,
                snapshot.ModifiedAtUtc,
                result
            )
        );
    }
);

Console.WriteLine("POST /imports with { \"tenant\": \"acme\", \"importId\": \"2026-06\", \"fileName\": \"customers.csv\" }");
Console.WriteLine("Then poll GET /imports/{jobRef}");

await app.RunAsync();

namespace Acta.Concepts.AspNetEnqueueApi
{
    public sealed record StartImportRequest(string Tenant, string ImportId, string FileName);

    public sealed record StartImportResponse(JobRef JobRef, string Action, string StatusUrl, string CorrelationKey);

    public sealed record ImportStatusResponse(
        JobRef JobRef,
        string Status,
        string? DeduplicationKey,
        DateTime CreatedAtUtc,
        DateTime ModifiedAtUtc,
        ImportResult? Result
    );

    public sealed record ImportCsv(string Tenant, string ImportId, string FileName);

    public sealed record ImportResult(int RowsImported);

    public static class ImportCsvJob
    {
        [Job("import-csv")]
        public static async Task<ImportResult> Handle(ImportCsv input, CancellationToken ct)
        {
            Console.WriteLine($"[{input.Tenant}] importing {input.FileName} ({input.ImportId})");
            await Task.Delay(750, ct);
            return new ImportResult(RowsImported: 42);
        }
    }
}
