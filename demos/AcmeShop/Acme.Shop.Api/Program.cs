using Acme.Shop;
using Acme.Shop.Api;
using Acme.Shop.Api.Domain;
using Acme.Shop.Payments.Contracts;
using Acta;
using Acta.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Acme Shop's own order store (App business records). Separate from Acta's execution state.
builder.Services.AddSingleton<IOrderStore, InMemoryOrderStore>();

builder.Services.UseActa(j => j.UseLocalDatabase(builder.Configuration));

var app = builder.Build();

// Serve the operator console (wwwroot/index.html) at the site root.
app.UseDefaultFiles();
app.UseStaticFiles();

// Accept an order. Identity comes from your auth middleware; here the X-User-Id header stands in.
app.MapPost(
    "/orders",
    async (OrderRequest input, HttpContext http, IOrderStore orders, IJobs jobs, CancellationToken ct) =>
    {
        var userId = http.Request.Headers["X-User-Id"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Results.Unauthorized();
        }

        if (
            string.IsNullOrWhiteSpace(input.OrderId)
            || input.Amount <= 0
            || input.Lines is not { Count: > 0 }
            || input.Lines.Any(l => string.IsNullOrWhiteSpace(l.Sku) || l.Quantity <= 0)
        )
        {
            return Results.BadRequest(new { error = "OrderId, a positive amount, and at least one valid line are required." });
        }

        // App records the order first, then hands execution to Acta. Save inserts only when new, so intake
        // is idempotent (matching the durable enqueue below) and the placed event is appended once.
        var placedAtUtc = DateTimeOffset.UtcNow;
        if (orders.Save(new OrderRecord(input.OrderId, userId, input.Amount, OrderRecord.OrderStatus.Pending, placedAtUtc)))
        {
            orders.Append(new OrderEvent(input.OrderId, "order-placed", placedAtUtc));
        }

        var request = new JobEnqueueRequest(
            "payments",
            "process-payment",
            JobPayload.Json(new OrderV1(input.OrderId, userId, input.Amount, input.Lines)),
            DeduplicationKey: $"payment:{userId}:{input.OrderId}",
            CorrelationKey: http.TraceIdentifier,
            Tags: [new TagInput("kind", "order")]
        );

        var outcome = await jobs.EnqueueAsync(request, ct);
        var statusUrl = $"/orders/{outcome.JobRef}";
        return Results.Accepted(statusUrl, new OrderAccepted(outcome.JobRef.ToString(), outcome.Action.ToString(), statusUrl));
    }
);

// Poll payment lifecycle by the public JobRef while the job is still retained.
app.MapGet(
    "/orders/{jobRef}",
    async (string jobRef, IJobs jobs, CancellationToken ct) =>
    {
        if (!JobRef.TryParse(jobRef, out var parsed))
        {
            return Results.BadRequest(new { error = "Invalid job ref." });
        }

        var snapshot = await jobs.GetAsync(parsed, ct);
        return snapshot is null ? Results.NotFound() : Results.Ok(ToStatus(snapshot));
    }
);

// Look up shipping by its user-scoped deduplication key. Requires the caller's identity so a predictable
// orderId alone cannot reveal another user's shipment.
app.MapGet(
    "/orders/{orderId}/shipping",
    async (string orderId, HttpContext http, IJobs jobs, CancellationToken ct) =>
    {
        var userId = http.Request.Headers["X-User-Id"].ToString();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Results.Unauthorized();
        }

        var snapshot = await jobs.GetAsync(JobLookup.ByDeduplicationKey("shipping", $"ship:{userId}:{orderId}"), ct);
        return snapshot is null ? Results.NotFound() : Results.Ok(ToStatus(snapshot));
    }
);

// Operator-only: approve or reject a held high-value order by raising the fixed fraud-review signal.
// In production this sits behind admin auth.
app.MapPost(
    "/admin/orders/{jobRef}/fraud-decision",
    async (string jobRef, FraudDecisionV1 decision, IJobs jobs, CancellationToken ct) =>
    {
        if (!JobRef.TryParse(jobRef, out var parsed))
        {
            return Results.BadRequest(new { error = "Invalid job ref." });
        }

        var result = await jobs.RaiseSignalAsync(parsed, "fraud-review", decision, ct: ct);
        return Results.Ok(new { raised = result.ToString() });
    }
);

// Live operations dashboard (read plus controls). Local-only by default; remote requests get 403.
app.MapActa("/acta", o => o.EnableControls = true);

Console.WriteLine("Acme Shop API: console at http://localhost:5000  -  dashboard at http://localhost:5000/acta");
Console.WriteLine("Enqueue-only. Run the workers in other terminals (or use the VS multi-project launch):");
Console.WriteLine("  dotnet run --project demos/AcmeShop/Acme.Shop.Payments");
Console.WriteLine("  dotnet run --project demos/AcmeShop/Acme.Shop.Shipping");

await app.RunAsync();

static JobLifecycleStatus ToStatus(JobSnapshot s) => new(s.JobRef.ToString(), s.Status.ToString(), s.CreatedAtUtc, s.ModifiedAtUtc);
