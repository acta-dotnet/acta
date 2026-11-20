<!-- engineering-lab
lab: can-jobs-choose-a-format-other-than-json
views: jobs_view
alternatives: json, gzip-json, custom-scalar
-->

# Engineering Lab: payload format is a durable contract choice

## The problem

One mandatory JSON representation is convenient, but it can waste bytes and CPU for dense or tiny
payloads. Choosing only the smallest encoding, however, can make operations and contract evolution much
harder.

## Common approaches

| Format | Strength | Cost |
| --- | --- | --- |
| JSON | Inspectable, interoperable, easy to evolve | Larger and often slower |
| Binary codec (MessagePack, protobuf) | Compact and fast | A package to depend on, and less SQL/operator readability |
| Gzip JSON | Preserves the JSON model with fewer bytes | CPU cost and no direct readability |
| Custom scalar | Minimal representation | Tight contract and migration burden |

## Why this design

Acta makes the codec name/identifier part of the durable job definition. Custom serializers are
registered explicitly and source-validated. The runtime can therefore round-trip several formats
without pretending that opaque bytes have no contract.

## Trade-offs

Every long-lived format needs compatibility, deployment-order, and migration discipline. Compact custom
formats reduce operator readability, and compression may cost more CPU than the saved storage is worth.

## Run the experiment

```bash
dotnet run --project learn/500-payloads/501-payload-formats
```

The program runs a built-in JSON baseline plus two custom formats, gzip JSON and a custom scalar, times
serialization separately from the batch-enqueue round trip, waits for completion, and executes the printed
byte-size comparison. Both custom serializers are plain .NET with no package: a binary codec plugs into the
same `IJobPayloadSerializer` seam. Every run gets a fresh correlation key, so completion waits and
measurements exclude historical rows.

## Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

`jobs_view` shows the durable format and whether input is human-readable. The second query deliberately
descends to `jobs`/`jobs_state` to measure physical bytes; `{{bytes:...}}` and `{{schema}}` are expanded by
the lab for SQLite, PostgreSQL, or SQL Server. Base tables are internal, not an application query API.

## Break it

Remove one serializer registration and observe startup/contract failure rather than silent decoding with
the wrong codec. Change the payload shape without a compatibility plan and consider how old rows decode.

## When not to use

Stay with JSON when volume is modest, inspection matters, or ecosystem interoperability dominates. Use
blob/object references rather than any inline codec for genuinely large payloads. Benchmark realistic
data before choosing compression.

## Source trail

- [The related Engineering Lab](../../../docs/engineering-labs.md)
- [`Primer.cs`](./Primer.cs)
- [`ScalarV1Serializer.cs`](./Serializers/ScalarV1Serializer.cs)
- [`JobPayloadSerializerRegistry.cs`](../../../src/Acta/Payloads/JobPayloadSerializerRegistry.cs)
- [`ActaManifestGenerator.cs`](../../../src/Acta.Generators/Jobs/ActaManifestGenerator.cs)

## Remember

- The names in `[Job("echo-scalar")]`, `[Job("echo-gzip")]` and `[Job("echo-json")]`, and the namespace in `Run<PayloadFormatsJobs>("payload-formats")`, are stored with every job. Treat them as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `EchoGzip` is stored with each job as its input and as its result. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `echo-gzip-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).

## Check yourself

**Jobs are stored in one payload format and you change the handler to another. What do the stored jobs need?**

- Nothing, the format is read from the new code
- A new versioned job name, with the old handler kept until its jobs have finished
- A change to the database

<details><summary>Answer</summary>

A new versioned job name, with the old handler kept until its jobs have finished. The format is stored with each job, so a handler that reads another format is another job.

</details>
