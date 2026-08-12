# Architecture diagrams

Diagram-first map of Acta: work flow, durability, and leaderless coordination. This visualizes the
model defined in [`concepts.md`](../guide/concepts.md); the rationale is in [`design.md`](../internals/design.md).

## Diagram inventory

| # | Diagram | Question it answers |
|---:|---|---|
| 1 | System context | What external pieces exist, and what does Acta own? |
| 2 | Runtime composition | Which runtime collaborators own which responsibilities? |
| 3 | Claim -> dispatch -> execute -> complete | What happens to one job attempt? |
| 4 | Job lifecycle state machine | Which states can a job occupy and why? |
| 5 | Persistence model tiers | Which tables are core work units, internal substrate, catalog, and operator views? |
| 6 | Maintenance and recovery | What happens after crashes, stale leases, dead workers, and retention windows? |

The conceptual model (four claims, the three identities, durable slots) lives in
[`design.md`](../internals/design.md) and [`concepts.md`](../guide/concepts.md); this doc shows the
runtime and persistence spine rather than re-stating it.

## 1. System context

```mermaid
flowchart LR
    subgraph App[Application process]
        API[App code / HTTP API]
        Jobs[IJobs facade]
        Handlers[[Job handlers]]
        Dashboard[Optional dashboard]
        CLI[Optional CLI]
    end

    subgraph Runtime[Acta worker runtime]
        WorkerRuntime[WorkerRuntime]
        WorkerLoop[WorkerLoop]
        WorkerHeartbeat[WorkerHeartbeat]
        JobExecutor[JobExecutor]
        JobRunner[JobRunner]
    end

    subgraph Storage[Durable SQL substrate]
        DB[(SQL Server / Postgres)]
        JobTable[(acta.jobs + acta.runtimes)]
        EventTable[(acta.events)]
        Substrate[(steps / checkpoints / leases)]
        Catalog[(definition / namespace / worker / schedule)]
    end

    Wakeup["Optional wakeup transport (Redis or in-process polling)"]
    Alerts["Alert transports (log / Slack / custom)"]
    Operators[Operators / DBAs]

    API --> Jobs
    Jobs --> DB
    WorkerRuntime --> WorkerLoop
    WorkerRuntime --> WorkerHeartbeat
    WorkerLoop --> JobExecutor
    JobExecutor --> JobRunner
    JobRunner --> Handlers
    JobRunner --> DB
    WorkerHeartbeat --> DB
    Wakeup -. nudges .-> WorkerLoop
    Dashboard --> DB
    CLI --> DB
    Operators --> Dashboard
    Operators --> CLI
    Operators --> DB
    DB --- JobTable
    DB --- EventTable
    DB --- Substrate
    DB --- Catalog
    DB --> Alerts
```

### Key points

- The SQL database is the durable coordination boundary.
- Operators and Acta runtime read the same tables.
- Wakeup is an optimization, not the source of truth.
- Workers are peers; there is no supervisor, scheduler daemon, or leader process.

## 2. Runtime composition

```mermaid
flowchart LR
    Host[IHostedService / app host] --> Runtime[WorkerRuntime]

    Runtime --> Initializer["WorkerRuntimeInitializer (catalog upsert + worker row)"]
    Runtime --> Loop["WorkerLoop (claim producer + executor channel)"]
    Runtime --> Heartbeat["WorkerHeartbeat (lease extension + cancellation propagation)"]
    Runtime --> Context["WorkerContext (registered namespaces, definitions, running attempts)"]

    Loop --> Claim[ClaimBatch operation]
    Loop --> Executor[JobExecutor]
    Executor --> Descriptor[JobDescriptor lookup]
    Executor --> AttemptScope[Per-attempt DI scope]
    Executor --> RuntimeCtx[RuntimeJobContext]
    Executor --> Runner[JobRunner]

    Runner --> Start[StartExecution operation]
    Runner --> Invoke["Generated descriptor.Invoker wrapped by pipeline behaviors"]
    Runner --> Complete[CompleteExecution operation]
    Runner --> Wake[WorkerWakeupPublisher]

    Heartbeat --> Extend[ExtendWorkerLeases operation]
    Heartbeat --> Cancel["Cancel running attempts that lost their lease"]
```

### Key points

- `WorkerRuntime` is a facade/composition root, not the engine itself.
- `WorkerLoop` owns claiming and concurrency fan-out.
- `JobExecutor` prepares one already-claimed job attempt.
- `JobRunner` owns the start -> invoke -> complete attempt lifecycle.
- `WorkerHeartbeat` is intentionally independent of executor throughput so long-running handlers do not starve lease extension.

## 3. Claim -> dispatch -> execute -> complete

```mermaid
sequenceDiagram
    autonumber
    participant Producer as App / IJobs
    participant DB as SQL database
    participant Wake as Wakeup transport
    participant WLoop as WorkerLoop
    participant Exec as JobExecutor
    participant Runner as JobRunner
    participant Handler as Job handler

    Producer->>DB: EnqueueOne/EnqueueBatch inserts acta.jobs + its runtimes row as Ready
    Producer-->>Wake: publish wakeup hint
    WLoop->>Wake: wait or safety poll
    WLoop->>DB: ClaimBatch, Ready -> Dispatched, lease + version bump
    DB-->>WLoop: claimed rows
    WLoop->>Exec: bounded channel dispatch
    Exec->>Exec: resolve descriptor, create attempt scope, build context
    Exec->>Runner: run claimed job
    Runner->>DB: StartExecution CAS, Dispatched -> Executing, append started event
    Runner->>Handler: descriptor.Invoker(input, ctx, ct)
    Handler-->>Runner: result / exception / suspend signal
    Runner->>DB: CompleteExecution writes result, appends finished event, sets next status
    Runner-->>Wake: publish wakeup if more work may be ready
```

### Key points

- The claim is the competitive admission point.
- `StartExecution` uses a version/CAS guard so stale attempts cannot start after the row has moved on.
- The event ledger gets paired execution-started and execution-finished events for attempts.
- Completion owns retry, terminal-state, recurring rollover, result persistence, and wakeup implications.

## 4. Job lifecycle state machine

```mermaid
stateDiagram-v2
    [*] --> Ready: enqueue / resume / retry due / schedule due

    Ready --> Dispatched: ClaimBatch
    Dispatched --> Executing: StartExecution

    Executing --> Succeeded: succeeded
    Executing --> Ready: failed within retry budget
    Executing --> Ready: rescheduled
    Executing --> Suspended: wait signal
    Executing --> Ready: sleep armed until due
    Executing --> Paused: handler pause
    Executing --> Failed: max attempts exhausted / handler fail / timeout exhausted
    Executing --> Cancelled: handler cancel / external cancel

    Dispatched --> Ready: maintenance reclaim after lease expiry
    Executing --> Ready: maintenance reclaim after lease expiry

    Suspended --> Ready: signal raised
    Paused --> Ready: resume
    Ready --> Cancelled: external cancel
    Paused --> Cancelled: external cancel
    Suspended --> Cancelled: external cancel

    Succeeded --> [*]
    Failed --> [*]
    Cancelled --> [*]
```

### Key points

- `Ready` is the only claimable state.
- `Dispatched` and `Executing` are active lease-owned states.
- `Succeeded`, `Failed`, and `Cancelled` are terminal states.
- `Suspended` means waiting on an external signal; sleep/reschedule can also re-arm as `Ready` with a future due time.
- The execution outcome (`Succeeded`, `Failed`, `Orphaned`, `Rescheduled`, `Suspended`, `Paused`, etc.) is recorded in the execution ledger.

## 5. Persistence model tiers

```mermaid
erDiagram
    JOB_NAMESPACE ||--o{ JOB_DEFINITION : owns
    JOB_NAMESPACE ||--o{ JOB_WORKER : registers

    JOB_DEFINITION ||--o{ JOB : instantiates
    JOB_DEFINITION ||--o{ JOB_SCHEDULE : declares

    JOB ||--|| JOB_RUNTIME : tracks
    JOB ||--o{ JOB_EVENT : emits
    JOB ||--o{ JOB_RESULT : stores
    JOB ||--o{ JOB_TAG : indexes
    JOB ||--o{ JOB_STEP : has
    JOB ||--o{ JOB_CHECKPOINT : has
    JOB ||--o{ JOB_ALERT : raises

    JOB ||--o{ LEASE : protects

    JOB_NAMESPACE {
      short id
      string name
    }
    JOB_DEFINITION {
      int id
      string job_name
      policy retry_timeout_schedule_payload
    }
    JOB {
      long id
      guid job_ref
      bytes input
      datetime created_at_utc
    }
    JOB_RUNTIME {
      long job_id
      status status_code
      int execution_number
      datetime next_run_at_utc
      int version
    }
    JOB_EVENT {
      long job_id
      short event_code
      short execution_number
      datetime created_at_utc
    }
    JOB_RESULT {
      long job_id
      short execution_number
      bytes result
    }
```

### Key points

- `jobs` is the only independently claimable work unit; its mutable runtime state (status, cursor, CAS version) lives on the 1:1 `runtimes` row.
- `events` is both lifecycle timeline and execution ledger.
- Internal substrate tables (`steps`, `checkpoints` for variables/signals/timers/progress) hang off a parent job and share its claim, lease, retry, cancellation, and audit lifecycle; they are never separately claimable.
- Catalog tables describe what can run; worker tables describe who is alive; operator tables describe alerts and controls.
- The full column-level model is generated in [`data-model.md`](../reference/data-model.md).

## 6. Maintenance and recovery

```mermaid
flowchart LR
    subgraph Workers[Peer workers]
        W1[Worker A]
        W2[Worker B]
        W3[Worker C]
    end

    subgraph SQL[SQL state]
        Job[("runtimes (status + execution lease)")]
        Worker[("workers (last_seen_at_utc)")]
        Lease[("leases (named locks)")]
        Event[("events (worker.dead / execution.finished)")]
    end

    subgraph SystemJobs[System maintenance jobs]
        Maintenance[sys.recovery]
        Alerts[sys.alerts]
        Purge[sys.retention]
    end

    W1 -->|heartbeat| Worker
    W1 -->|extend execution leases| Job
    W1 -->|extend locks| Lease

    W2 -->|claims like ordinary work| Maintenance
    W3 -->|claims like ordinary work| Alerts
    W2 -->|claims like ordinary work| Purge

    Maintenance -->|mark stale workers dead| Worker
    Maintenance -->|reclaim expired active jobs| Job
    Maintenance -->|append orphaned / worker events| Event
    Alerts -->|materialize deliverable alerts| Event
    Purge -->|retention cleanup| Job
```

### Key points

- Maintenance is work, not leadership.
- Any peer can claim maintenance jobs through the same durable claim path as user jobs.
- Stale workers are detected through heartbeat state.
- Expired leases cause orphan/reclaim behavior; handlers still own side-effect idempotency.

## Review checklist for diagram drift

Before publishing a release, verify these diagrams still match source:

- Job statuses and execution statuses still match the code families.
- `WorkerRuntime`, `WorkerLoop`, `JobExecutor`, `JobRunner`, and `WorkerHeartbeat` still own the responsibilities shown here.
- The persistence diagram still matches [`data-model.md`](../reference/data-model.md) and the generated migrations.
- Maintenance jobs are still ordinary jobs claimed through the normal path.
- Provider parity is still tested through shared conformance specs for both SQL Server and Postgres.
