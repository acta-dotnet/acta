# Security policy

## Supported versions

Acta is in preview and has not reached 1.0. Only the **latest published version** receives fixes;
there are no backports to earlier previews, and a preview may be superseded without a migration path
while the schema baseline is still re-cuttable.

| Version | Supported |
|---|---|
| Latest preview | Yes |
| Earlier previews | No |

This tightens at 1.0, when the schema, persisted codes, and public API freeze together and a
supported-versions window is stated properly. The support matrix, supported .NET target, and the
patch policy that takes effect at 1.0 are stated in [docs/support.md](./docs/support.md).

## Reporting a vulnerability

Report privately through GitHub's **[Report a vulnerability](https://github.com/acta-dotnet/acta/security/advisories/new)**
form, which opens a private advisory visible only to the maintainer. Please do not open a public
issue for a security problem.

Useful in a report: the affected version, the provider (PostgreSQL / SQL Server / SQLite), whether
the dashboard or HTTP API is exposed beyond localhost, and the smallest reproduction you have.

## What to expect

Acta is maintained by one person. The commitment is best-effort rather than a contractual
SLA: acknowledgement when the report is read, an assessment of whether it is exploitable and how,
and a fix in the next published version once confirmed. If a report turns out not to be a
vulnerability, you will get the reasoning rather than silence.

## Scope

Things that are in scope:

- Anything that lets one tenant's or namespace's work be read, claimed, or altered through another.
- SQL injection or parameter-binding escapes in any provider.
- Privilege escalation through the operator HTTP API or dashboard controls.
- A durable-state corruption reachable from ordinary API use.

Things that are **not** vulnerabilities, because they are documented design boundaries:

- **The dashboard and API ship without authentication.** They are local-only by default and fail
  closed when exposed remotely without an authorizer; the host owns authentication and
  authorization. Exposing them publicly without configuring that is a deployment choice, not a flaw.
  See `docs/guide/operator-guide.md`.
- **Execution is at-least-once.** A handler can run more than once, so external side effects need
  idempotency or reconciliation. See `docs/technical/known-limitations.md`.
- **The tenant field is not a security boundary.** Acta validates, persists, and propagates tenant
  identity; it does not authorize callers or filter application data. Separate residency or blast
  radius takes separate stores or deployments. See `docs/guide/concepts.md`.
- **Job payloads are stored as supplied.** Acta does not encrypt them; a payload containing secrets
  is readable by anyone with database access.
