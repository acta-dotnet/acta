<!-- engineering-lab
lab: can-a-tenant-be-an-audit-boundary-without-becoming-a-queue
views: jobs_view, events_view
alternatives: namespace-per-tenant, database-per-tenant, queue-partition, tags-only, tenant-catalog
-->

# Engineering Lab: tenant is who the work is about

## The problem

Multi-tenant work needs filtering, audit attribution, lifecycle guards, and child inheritance. A string
tag can label work, but it cannot reject an unknown or suspended customer atomically at enqueue.

## Common approaches

- Use a namespace/queue per tenant.
- Use a database per tenant for hard isolation.
- Partition a broker by tenant key.
- Attach unvalidated tags only.
- Resolve a tenant key through an Acta-owned catalog.

## Why this design

Namespace answers “who owns and executes this work?” Tenant answers “who is this work about?” Acta
resolves an active `TenantKey` to `tenant_id` on insert, stamps events, inherits it to children, exposes
tenant filtering, and rejects unknown or suspended tenants.

## Trade-offs

A shared database/catalog is an audit and control boundary, not hard data isolation. Existing in-flight
jobs are not cancelled when a tenant is suspended. Tenant cardinality and query/index pressure still
land on the shared store.

## Run the experiment

```bash
dotnet run --project concepts/400-observability-and-alerts/412-tenant-scope
```

## Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

The lab uses `jobs_view` and `events_view`, joining the internal tenant catalog only to decode the key.
It proves parent/child inheritance and also exercises the operator ledger read `IActaOperations.Ledger.ListJobsAsync` with its tenant filter and a fresh
correlation key, so repeated runs still report exactly the current parent and child. Application code
should use `IActaOperations.Tenants` and `IJobs`; the underlying catalog table is not a compatibility contract.

## Break it

The default run rejects both an unknown and an already suspended tenant. Also run:

```bash
dotnet run --project concepts/400-observability-and-alerts/412-tenant-scope -- --suspend-active
```

The mode accepts one delayed job while `acme` is active, suspends the tenant, proves a new enqueue is
rejected, and then proves the already accepted job was not cancelled. It resumes `acme` afterward so
the shared local database remains repeatable.

## When not to use

Use separate databases or stronger infrastructure isolation for regulatory/data-residency boundaries.
Use queue partitioning when tenant ordering and throughput isolation are the primary goals. Tags are
enough when the label needs no validation or lifecycle.

## Source trail

- [The related Engineering Lab](../../../docs/engineering-labs.md)
- [`tenant-scope.cs`](./tenant-scope.cs)
- [`ITenants.cs`](../../../src/Acta/Tenants/ITenants.cs)
- [`TenantEnqueueSpec.cs`](../../../tests/Acta.Tests.Conformance/Features/Jobs/TenantEnqueueSpec.cs)
