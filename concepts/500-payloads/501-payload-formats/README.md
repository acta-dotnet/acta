<!-- engineering-lab
lab: can-jobs-choose-messagepack-over-json
views: jobs_view
alternatives: json, messagepack, gzip-json, custom-scalar
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
| MessagePack | Compact and fast | Less SQL/operator readability |
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
dotnet run --project concepts/500-payloads/501-payload-formats
```

The program runs a built-in JSON baseline plus three custom formats, times serialization separately from
the batch-enqueue round trip, waits for completion, and executes the printed byte-size comparison. Every
run gets a fresh correlation key, so completion waits and measurements exclude historical rows.

## Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

`jobs_view` shows the durable format and whether input is human-readable. The second query deliberately
descends to `jobs`/`results` to measure physical bytes; `{{bytes:...}}` and `{{schema}}` are expanded by
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
- [`JobPayloadSerializerRegistry.cs`](../../../src/Acta.Runtime/Payloads/JobPayloadSerializerRegistry.cs)
- [`ActaManifestGenerator.cs`](../../../src/Acta.Generators/Features/Jobs/ActaManifestGenerator.cs)
