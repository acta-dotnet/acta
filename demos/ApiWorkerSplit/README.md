# 706 — API/worker split

This production-shape capstone runs an enqueue-only ASP.NET Core API and an Acta worker as separate
processes. Both coordinate through the same durable database; neither calls the other.

```text
HTTP client -> API -----> Acta database <----- Worker
               |                              |
               +-- shared Contracts project -+
```

The API references the shared contract but contains no handler and starts no worker loop. The worker
owns the handler manifest and never exposes an HTTP endpoint. This is the deployment boundary that
[`701-enqueue-only-reference`](../../concepts/700-topology-and-deployment/701-enqueue-only-reference/)
introduces inside one process, now exercised across real processes.

## Why split the processes?

A single API-and-worker host is the simplest deployment and is often the right starting point. A split
becomes useful when HTTP traffic and background execution need independent scaling, failure isolation,
deployment cadence, or permissions.

Acta uses the relational database as the handoff and coordination authority. Acta atomically creates the
job row and its runtime state inside its own store operation without requiring a direct worker connection.
The job row's creation columns are the enqueue record; enqueue does not append a separate event. Acta's
public enqueue API does not automatically enlist in the application's existing business-data transaction,
so atomically committing that data and enqueueing a job still requires an application-level transaction or
outbox strategy. Job claims, heartbeats, and completions consume database capacity, and contract changes
must remain safe while old and new processes overlap during a deployment.

Use a broker or streaming platform instead when very high event throughput, broad fan-out, or log replay
is the central requirement. Use an external workflow engine when the process must be orchestrated across
many systems and teams rather than owned by one application boundary.

## Run with F5

Requirements: the .NET SDK selected by [`global.json`](../../global.json) and Visual Studio with `.slnx`
and shared multi-project launch-profile support.

1. Open [`ApiWorkerSplit.slnx`](./ApiWorkerSplit.slnx) in Visual Studio.
2. Select **API + worker** in the launch-profile dropdown.
3. Press **F5**. The worker and API start together; the API listens on `http://localhost:5000`.
4. Open [`ApiWorkerSplit.http`](./ApiWorkerSplit.http) and send the `POST /welcome-emails` request.
5. Copy the returned `jobRef` into the file's `@jobRef` variable and send the status request.

The POST response is `202 Accepted`: it confirms durable admission, not handler completion. The status
eventually becomes `Succeeded`, and only the worker console prints `Sent welcome email ...`.

Sending the same body again returns `action: "Deduplicated"` with the same `jobRef` because the API
derives the deduplication key from `userId`. Change `userId` when you want a new job.

## Run from terminals

Build the focused solution once:

```bash
dotnet build demos/ApiWorkerSplit/ApiWorkerSplit.slnx
```

Then keep these commands running in separate terminals:

```bash
dotnet run --project demos/ApiWorkerSplit/Worker
```

```bash
dotnet run --project demos/ApiWorkerSplit/Api
```

Use [`ApiWorkerSplit.http`](./ApiWorkerSplit.http) for the requests. Both processes default to the same
SQLite file, so no database server or connection string is required.

## Break the boundary

First let both processes start so the worker registers the durable definition.

1. Enqueue a new request and immediately stop the API. The worker can still finish the accepted job.
2. Restart the API, stop the worker, change `userId`, and enqueue again. The accepted job remains durable
   as `Ready` until worker capacity returns.
3. Restart the worker. It claims and completes the waiting identity as `Succeeded` without the API
   resubmitting it.

This demonstrates process independence, not exactly-once side effects. A worker can be lost after sending
an email but before recording completion, so production side effects still need idempotency or
reconciliation where duplication matters.

## Read the split

- [`Api/Program.cs`](./Api/Program.cs): raw route-based enqueue and durable status endpoint, with no worker
  registration.
- [`Contracts/SendWelcomeEmail.cs`](./Contracts/SendWelcomeEmail.cs): the shared route and wire contract.
- [`Worker/Program.cs`](./Worker/Program.cs): worker registration and namespace ownership.
- [`Worker/SendWelcomeEmailJob.cs`](./Worker/SendWelcomeEmailJob.cs): handler code available only to the
  worker process.
- [`Directory.Build.props`](./Directory.Build.props): the shared local provider and source references that
  keep the demo runnable from a repository checkout.
