# Packaged deployment smoke

Runs the packed packages the way a deployment does: a Kestrel host restored from a local feed with
no project references, mounted under a path base, behind a real nginx proxy in Docker, with an
authorization policy on the dashboard. It proves the packages work as consumed and that the
dashboard's protected surface stays protected once a proxy and a prefix sit in front of it.

```powershell
./tests/DeploymentSmoke/run.ps1 -PackageVersion <packed-version>
```

The consumer restores `Acta.*` only from `artifacts/packages`, so that feed must hold the exact
packages under test: the `packages` artifact of a CI run, or a local pack of `Acta`, `Acta.Runtime`,
`Acta.Relational`, `Acta.Sqlite`, and `Acta.AspNetCore` with `-c Release -o artifacts/packages`.
`Acta.AspNetCore` embeds the dashboard at pack time and needs Node, or an existing `dist` with
`-p:ActaDashboardSkipNpm=true`. Like `tests/PackageSmoke`, this tree builds as an external consumer
would; its build props say why.

## What it checks

The host runs an isolated SQLite database, completes one job whose input, result, and checkpoint are
raw bytes, maps the dashboard read-only at `/operations/acta` through `UsePathBase`, and writes its
port. nginx then proxies `/operations/` to it, prefix preserved, on a loopback-only published port.

- Anonymous requests to the page, an asset path, the job list, and the job's input and detail all
  answer 401, including with spoofed forwarded-user and forwarded-proto headers.
- An authorized request loads the page, and the JavaScript asset the page names loads with a real
  body; the same asset anonymously answers 401, so the packaged asset route is inside the policy.
- Authorized input and detail reads return the exact bytes enqueued, returned, and checkpointed, and
  the detail response is marked `no-store`.
- Capabilities report controls disabled and the cancel endpoint is not mapped, so a read-only mount
  is read-only through the proxy.

`artifacts/deployment-smoke/<run>/evidence.json` records the package version and hashes, the proxy
image and its id, and each check. The app and proxy logs sit beside it. The script stops its own
process and its uniquely named container and nothing else.

## What it does not claim

Authentication is a test-only header scheme, over plain HTTP, with no TLS termination. The run says
nothing about a production identity provider, other proxies, or browser behaviour.
