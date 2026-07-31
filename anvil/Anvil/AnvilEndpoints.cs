using System.Net;
using System.Text.Json.Serialization;
using Acta;

namespace Anvil;

/// <summary>Loopback-only process and workload controls for the Anvil cockpit.</summary>
public static class AnvilEndpoints
{
    private static readonly SemaphoreSlim RunStart = new(1, 1);

    public static RouteGroupBuilder MapAnvil(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/anvil/api");
        GuardLoopback(group);
        CatchErrors(group);

        group.MapGet("/state", async (AnvilStateReader reader, CancellationToken ct) => Results.Ok(await reader.ReadAsync(ct)));

        group.MapPost(
            "/workers",
            (WorkerProcessLauncher launcher) =>
            {
                var worker = launcher.Spawn();
                return Results.Ok(new ActionResponse($"Spawned {worker.Name}."));
            }
        );

        group.MapPost(
            "/workers/{id:int}/crash",
            (int id, WorkerProcessLauncher launcher) =>
                launcher.Crash(id)
                    ? Results.Ok(new ActionResponse($"Crashed worker-{id}."))
                    : Results.NotFound(new ActionResponse($"No crashable managed worker with id {id}."))
        );

        group.MapPost(
            "/workers/{id:int}/drain",
            (int id, WorkerProcessLauncher launcher) =>
                launcher.Stop(id)
                    ? Results.Ok(new ActionResponse($"worker-{id} is draining."))
                    : Results.NotFound(new ActionResponse($"No drainable managed worker with id {id}."))
        );

        group.MapPost("/faults/crashes/start", (FaultInjectors faults) => Results.Ok(new ActionResponse(faults.StartContinuousCrashes())));
        group.MapPost("/faults/crashes/stop", (FaultInjectors faults) => Results.Ok(new ActionResponse(faults.StopContinuousCrashes())));
        group.MapPost(
            "/faults/pressure/start",
            (QueuePressureRequest request, FaultInjectors faults) =>
                request.JobsPerSecond is 1_000 or 10_000
                    ? Results.Ok(new ActionResponse(faults.StartQueuePressure(request.JobsPerSecond)))
                    : Results.BadRequest(new ActionResponse("Queue pressure supports 1,000 or 10,000 jobs per second."))
        );
        group.MapPost("/faults/pressure/stop", (FaultInjectors faults) => Results.Ok(new ActionResponse(faults.StopQueuePressure())));
        group.MapPost(
            "/faults/outbox/start",
            (QueuePressureRequest request, FaultInjectors faults) =>
                request.JobsPerSecond is 1_000 or 10_000
                    ? Results.Ok(new ActionResponse(faults.StartOutboxPressure(request.JobsPerSecond)))
                    : Results.BadRequest(new ActionResponse("Outbox pressure supports 1,000 or 10,000 rows per second."))
        );
        group.MapPost("/faults/outbox/stop", (FaultInjectors faults) => Results.Ok(new ActionResponse(faults.StopOutboxPressure())));

        group.MapPost(
            "/run",
            async (
                AnvilRunSpec spec,
                WorkerProcessLauncher launcher,
                AnvilSession session,
                SeedProgress progress,
                IServiceScopeFactory scopes,
                IActaOperations operations,
                CancellationToken ct
            ) =>
            {
                if (Validate(spec) is { } validationError)
                {
                    return Results.BadRequest(new ActionResponse(validationError));
                }
                var workers = await operations.Workers.ListAsync(new ListWorkersQuery(session.NamespaceName, PageSize: 1), ct);
                if (workers.Items.Count == 0)
                {
                    return Results.Conflict(new ActionResponse("Wait for the first worker to register definitions, then start the run."));
                }

                await RunStart.WaitAsync(ct);
                try
                {
                    if (progress.Snapshot().Active)
                    {
                        return Results.Conflict(new ActionResponse("A seed run is already in progress."));
                    }

                    launcher.SetTargetCount(spec.WorkerCount);
                    var batch = session.NextBatch();
                    // SeedAsync marks progress active before its first yield, so the serialized admission
                    // check cannot release another run until this one visibly owns the progress slot.
                    _ = SeedInBackgroundAsync(scopes, batch, spec, progress);

                    return Results.Accepted(
                        "/anvil/api/state",
                        new RunAcceptedResponse(Accepted: true, Target: spec.Load, Workload: spec.Workload)
                    );
                }
                finally
                {
                    RunStart.Release();
                }
            }
        );

        return group;
    }

    private static async Task SeedInBackgroundAsync(IServiceScopeFactory scopes, int batch, AnvilRunSpec spec, SeedProgress progress)
    {
        using var scope = scopes.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<AnvilSeeder>();
        try
        {
            await seeder.SeedAsync(batch, spec, progress, CancellationToken.None);
        }
        catch
        {
            // SeedAsync records a one-line error in SeedProgress for the polling UI.
        }
    }

    private static string? Validate(AnvilRunSpec spec)
    {
        if (spec.WorkerCount is < 1 or > 8)
        {
            return "Worker processes must be between 1 and 8.";
        }

        var validLoad = spec.Workload switch
        {
            AnvilWorkloadCode.NoOp => spec.Load is 10_000 or 100_000 or 1_000_000,
            AnvilWorkloadCode.Steady => spec.Load is 1_000 or 10_000 or 100_000,
            AnvilWorkloadCode.CrashRecovery => spec.Load is 100 or 1_000 or 10_000,
            AnvilWorkloadCode.RetryAndFailure => spec.Load is 1_000 or 10_000 or 100_000,
            AnvilWorkloadCode.FanOut => spec.Load is 10 or 100 or 1_000,
            _ => false,
        };
        return validLoad
            ? null
            : spec.Workload switch
            {
                AnvilWorkloadCode.NoOp => "No-op supports 10,000, 100,000, or 1,000,000 jobs.",
                AnvilWorkloadCode.Steady => "Steady supports 1,000, 10,000, or 100,000 jobs.",
                AnvilWorkloadCode.CrashRecovery => "Crash Recovery supports 100, 1,000, or 10,000 jobs.",
                AnvilWorkloadCode.RetryAndFailure => "Retry and Failure supports 1,000, 10,000, or 100,000 jobs.",
                AnvilWorkloadCode.FanOut => "Fan-out supports 10, 100, or 1,000 parent jobs.",
                _ => "Unknown workload.",
            };
    }

    private static void CatchErrors(RouteGroupBuilder group) =>
        group.AddEndpointFilter(
            async (context, next) =>
            {
                try
                {
                    return await next(context);
                }
                catch (Exception ex)
                {
                    var line = ex.Message.Split('\n', '\r')[0].Trim();
                    return Results.Json(
                        new ActionResponse(line.Length > 160 ? line[..160] : line),
                        AnvilJsonContext.Default.ActionResponse,
                        statusCode: StatusCodes.Status503ServiceUnavailable
                    );
                }
            }
        );

    private static void GuardLoopback(RouteGroupBuilder group) =>
        group.AddEndpointFilter(
            async (context, next) =>
            {
                var remote = context.HttpContext.Connection.RemoteIpAddress;
                return remote is null || IPAddress.IsLoopback(remote)
                    ? await next(context)
                    : Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "The lab controls are loopback-only.");
            }
        );
}

public sealed record ActionResponse(string Message);

public sealed record RunAcceptedResponse(bool Accepted, int Target, AnvilWorkloadCode Workload);

public sealed record QueuePressureRequest(int JobsPerSecond);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never
)]
[JsonSerializable(typeof(AnvilState))]
[JsonSerializable(typeof(AnvilRunSpec))]
[JsonSerializable(typeof(RunAcceptedResponse))]
[JsonSerializable(typeof(ActionResponse))]
[JsonSerializable(typeof(QueuePressureRequest))]
internal sealed partial class AnvilJsonContext : JsonSerializerContext;
