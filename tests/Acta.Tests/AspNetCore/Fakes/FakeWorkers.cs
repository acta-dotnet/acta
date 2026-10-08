namespace Acta.Tests.AspNetCore.Fakes;

/// <summary><see cref="IWorkers"/> over one worker, <see cref="KnownWorkerRef"/> (internal id 42), and an empty list.</summary>
internal sealed class FakeWorkers : IWorkers
{
    /// <summary>The one worker the fake knows; every other ref reports not found.</summary>
    public static readonly WorkerRef KnownWorkerRef = new(new Guid("019826f0-0000-7000-8000-0000000002a0"));

    public FakeWorkers()
    {
        Get = static (workerRef, _) => ValueTask.FromResult<GetWorkerResponse?>(workerRef == KnownWorkerRef ? Known : null);
        List = static (_, _) => ValueTask.FromResult(new ListWorkersResponse([], null, false, 50, null));
    }

    public Func<WorkerRef, CancellationToken, ValueTask<GetWorkerResponse?>> Get { get; set; }

    public Func<ListWorkersQuery, CancellationToken, ValueTask<ListWorkersResponse>> List { get; set; }

    ValueTask<GetWorkerResponse?> IWorkers.GetAsync(WorkerRef workerRef, CancellationToken ct) => Get(workerRef, ct);

    ValueTask<ListWorkersResponse> IWorkers.ListAsync(ListWorkersQuery query, CancellationToken ct) => List(query, ct);

    private static readonly GetWorkerResponse Known = new(
        KnownWorkerRef,
        42,
        "billing",
        WorkerStatusCode.Active,
        "worker-host",
        "deploy-42",
        "engine-1",
        ".NET 10.0.0",
        4242,
        8,
        new DateTime(2026, 6, 12, 8, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 6, 12, 7, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 6, 12, 8, 0, 0, DateTimeKind.Utc)
    );
}
