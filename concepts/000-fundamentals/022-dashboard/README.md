<!-- engineering-lab
lab: can-controls-be-opt-in-by-default
views: jobs_view, events_view, schedules_view, workers_view, alerts_view
alternatives: separate-dashboard, embedded-read-only-ui, enabled-controls, custom-operator-api
-->

# Engineering Lab: an embedded dashboard with a safe default

## The problem

A useful job UI needs representative runtime states, but mapping mutation endpoints casually can turn an
observability feature into an unaudited production control surface.

## Common approaches

- Deploy a separate dashboard/control service.
- Embed a read-only UI beside the application.
- Enable controls explicitly and protect them with operator authorization.
- Build a domain-specific operator API and UI.

## Why this design

`MapActa` ships the API and dashboard assets with the app that owns the jobs. This lab seeds success,
failure, retry, signal-waiting, recurring, parent, and child states. Controls remain disabled unless the
configuration switch is intentionally set. Expected handler warnings are suppressed in the console so
the startup URL remains obvious; their durable failures and full event evidence remain in Acta.

## Trade-offs

Embedding ties dashboard lifecycle and resource use to the host. It is not a substitute for centralized
authentication, authorization, network policy, or fleet-wide aggregation. Enabling controls expands the
application's operational attack surface.

## Run the experiment

```bash
dotnet run --project concepts/000-fundamentals/022-dashboard
```

Then opt in locally and compare:

```bash
Acta__Dashboard__EnableControls=true dotnet run --project concepts/000-fundamentals/022-dashboard
```

In PowerShell, set `$env:Acta__Dashboard__EnableControls='true'` first.

## Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

The startup query shows the same `jobs_view` states rendered by the UI. Explore events, schedules,
workers, alerts, lineage, and explanations in the dashboard. These curated surfaces are for operations;
the internal storage tables are not a public compatibility contract.

## Break it

Try a mutation while controls are disabled, then opt in and repeat with the required confirmation
header/UI flow. Also bind beyond localhost only in a deliberately secured test environment.

## When not to use

Use a centralized operations product when one UI must span many deployments, databases, or trust zones.
Build a custom UI when operators need domain-specific workflows rather than generic job controls.

## Source trail

- [The controls Engineering Lab](../../../docs/engineering-labs.md)
- [The embedded-dashboard Engineering Lab](../../../docs/engineering-labs.md)
- [`Program.cs`](./Program.cs)
- [`ActaEndpointOptions.cs`](../../../src/Acta.AspNetCore/Configuration/ActaEndpointOptions.cs)
- [`ActaControlEndpoints.cs`](../../../src/Acta.AspNetCore/Features/Jobs/ActaControlEndpoints.cs)
