# Releasing

## Purpose

Checklist for preparing an Acta release. This is a manual process checklist; the only automated step
is publishing: pushing a `v*` tag makes CI pack and, once all jobs are green, push the packages to
nuget.org via Trusted Publishing (`publish-nuget` job in `ci.yml`, gated on the `release` environment).

## Build and test

- `dotnet restore Acta.slnx`
- `dotnet build Acta.slnx -c Release -p:ActaDashboardSkipNpm=true` (write the property as `-p:`,
  not `/p:`: a Git Bash shell rewrites a leading slash into a path, MSBuild rejects the switch, and
  the pipeline still exits 0, so the step reads as a passing build that compiled nothing)
- `dotnet test tests/Acta.Tests/Acta.Tests.csproj -c Release`
- Full provider tests with Docker.
- Dashboard `npm ci`, `npm test`, `npm run build`.
- `dotnet run --project tools/Acta.Emit -- check`
- `dotnet csharpier check .`
- Schema guard, pre-1.0: a re-cut `M001` is allowed. `schema amend` stamps each provider's `M001`
  with a hash of its own content and writes `src/Acta.Relational/Schema/BaselineStamps.g.cs` in the
  same pass, so a database from any earlier cut refuses to start instead of taking the re-cut as a
  no-op, and `Acta.Emit check` fails on a hand-edited `M001`. Nothing about the stamp is settled by
  hand; it moves with every re-cut, however small, so the last re-cut of a release happens before
  certification, never after.
- The release notes name the changed objects and say that every provider's database must be dropped
  and reprovisioned, since there is no upgrade path between generations before 1.0.
- Schema/code-freeze guard, from 1.0.0: the release diff contains no M001 edits and no destructive
  migration statements, renumbered code pairs, retired-id reuse, or closed-family `255` assignments;
  schema changes ship only as additive `Mnnn` migrations.

## Failure-boundary acceptance

Throughput does not stand in for these, and neither does a clean rerun of a stall nobody explained.
Each line names the evidence that carries it; a candidate ships with all of it recorded against the
certified commit.

- A completion write that keeps failing while the heartbeat still renews the lease settles once the
  provider returns, on the same worker, with no restart: `CompleteAndClockChaosSpec` and
  `WorkerCrashRecoveryChaosSpec` on every provider, `CompletionWriteTests` for the repeat itself, and
  `CompletionSinkBulkFallbackSpec` for a Bulk batch whose failing entry holds up nobody else.
- A completion that commits and loses its response is reconciled by the repeat, not rerun, and never
  advances a recurring slot twice: the after-commit case in `CompleteAndClockChaosSpec`.
- The recovery sweep runs while every executor is busy, because it runs outside the executor pool,
  and a stranded `sys.recovery` slot is re-armed under a guard by any live worker:
  `RecoverySlotMonitorSpec`.
- Incidents at the failures-only audit level stay open by design and the page says so:
  `FailuresAuditFailureEventSpec` pins that a success at that level writes no event and resolves
  nothing, so the documented limitation cannot change by accident.
- A build refuses a database whose installed object package it cannot call, and an older worker keeps
  running on the upgraded objects: `ObjectPackagePreflightTests` and `MigrationHistoryPreflightSpec`
  for the verdicts, and `tests/RollingUpgradeSmoke/run.ps1` against the previous tag on both servers
  for the real pair of binaries. Its `evidence.json` must record a clean worktree and matching commits.
- The packed packages deploy: `tests/PackageSmoke/run.ps1` on the final feed, then
  `tests/DeploymentSmoke/run.ps1` on the same feed for the dashboard behind a real proxy with a path
  base and an authorization policy. Its README says what the run does not cover.
- Load with maintenance on: a `tests/HardeningSoak` run per server provider with autovacuum on and
  retention sweeping under the workload, kept with its progress sidecar. Its README says how to read
  the numbers; they are not a benchmark round.

## Frozen contracts

Five baselines fail a test on drift, so the suite above already catches an accidental change. What a
release adds is the judgement the test cannot make: **read each moved baseline and confirm the move was
intended.** A regenerated baseline is indistinguishable from a deliberate one once committed.

| Contract | Baseline | Regenerate |
|---|---|---|
| .NET public surface | `tests/Acta.Tests/Contracts/PublicApiSurface.approved.txt` | `ACTA_EMIT_API=1 dotnet test tests/Acta.Tests --filter PublicApiContractTests` |
| HTTP surface | `docs/reference/openapi.json` | `ACTA_EMIT_OPENAPI=1 dotnet test tests/Acta.Tests --filter OpenApiContractTests` |
| Persisted codes | hash in `PersistedCodeContractTests` | re-pin the hash by hand |
| Conformance docs | `docs/reference/conformance-contracts.md` | `ACTA_EMIT_DOCS=1 dotnet test tests/Acta.Tests --filter DocsContractTests` |
| Baseline stamps | `src/Acta.Relational/Schema/BaselineStamps.g.cs`, one hash per provider `M001` | `dotnet run --project tools/Acta.Emit -- schema amend` (pre-1.0 only; from 1.0.0 a moved stamp means `M001` was edited and blocks the release) |
| Object package | `src/Acta.Relational/Schema/ObjectPackageHashes.g.cs` and `object-packages.json`, one content hash per provider's installed views and routines | `dotnet run --project tools/Acta.Emit -- objects record` after any view or routine edit; `check` refuses a recorded identity whose content moved. An identity is frozen the moment it is recorded, deliberately: before the first release that costs a revision bump per edit rather than tracking what shipped |

Before 1.0 a moved surface is allowed and belongs in the release notes. From 1.0 the .NET and HTTP
surfaces are additive-only, so a diff that removes or renames a member is a 2.0 change and blocks the
release.

## Certification

Deliberately not in CI: shared runners cannot host multi-process kill testing meaningfully. It is a
per-release gate run locally.

- One run per provider, PostgreSQL first. `anvil` with `--certify-jobs`, or an ensemble with
  `--run`/`--seed`/`--participant`/`--port` across participants.
- A seal is only meaningful with non-zero reclaims: a run shorter than the lease window plus the
  recovery cadence reports zero and is INCONCLUSIVE, not PASS.
- SQLite is single-node, so its run is reduced and its seal states which properties were out of scope.
- File the JSON/MD seal under `docs/certification/`.

Two commits carry a release, and their roles do not mix:

- **The certified code commit, A**, is the last commit that touches code, schema, routines, tests,
  tools, anvil, project files, or a generated artifact. The format check, `Acta.Emit check`, the unit
  suite, every provider conformance suite, the benchmarks, and, when the release certifies, the
  certification quartet and the coverage run all execute against exactly A. Every seal records A as
  the certified commit. A release may skip certification by decision when the execution model did not
  change; the release notes say so, and the benchmark round is then the gate.
- **The tag commit, B**, is the last of the documentation-only commits after A that file whatever
  evidence the release produced (seals, the benchmark and coverage pages, the certification index)
  and the release-notes header flip.
  `tools/release-guard.ps1 -CertifiedCommit <A> -TagCommit HEAD` runs before tagging and fails when
  `A..B` touches anything outside `docs/certification/`, `docs/benchmarks/`, `docs/release-notes.md`,
  `docs/README.md`, `site/` (the useacta.net benchmark strip cites the round), or this page.
- A defect found by any of A's runs abandons A: the fix goes back through its slice, a new candidate is
  cut, and every piece of evidence is re-run against it. Evidence from an abandoned candidate is never
  reused for the next one.

## Benchmark round

A round is evidence only when its files record the commit with `gitDirty = false` and every number
that looks like a finding has a same-hour control on the previous tag beside it. The rules below are
what the rounds on this machine cost to learn.

- Measure from a clean `git worktree` of the exact commit. The harness records `git status`, and an
  untracked file in the main tree marks every JSON of the round dirty.
- Leave PostgreSQL autovacuum on. A round that needed the server's own maintenance disabled to look
  good is not a round worth publishing, and the reason anyone wanted it off was a harness leak that is
  now fixed: each cell drops its schema when its measurement is recorded, so a `full` matrix leaves
  the reusable preflight probe and nothing else. Dropping is best effort, because a round that
  measured cleanly must not be failed by its own cleanup; the harness says which schema it could not
  drop, and a round interrupted part way can still leave some behind. Sweeping by hand is then the
  same job it always was: on PostgreSQL generate `DROP SCHEMA ... CASCADE` from `pg_namespace` and run
  it through `psql -f`; on SQL Server a cursor over `sys.schemas` that drops foreign keys, views,
  procedures, functions, tables, table types, and sequences before the schema; and delete the
  `acta-anvil-bench-*.db` files from the temp folder.
- Check the drive at the host, not only `pg_test_fsync` inside the container: two hundred 8 KB writes
  each followed by `Flush(true)` in the temp folder give flushes per second for the drive the Docker
  disk image lives on. The bench machine's C: drive is QLC and drops from about 2,800 to 300-500
  flushes per second after sustained writes, stays there for hours, and flaps between the two states,
  so take readings minutes apart and start only when several agree. A probe in a tight loop is itself
  a write load and keeps the drive from recovering.
- Interleave the trees: candidate, control on the previous tag, candidate again, per provider, so all
  three share the drive's state. A pair split across states is discarded whichever way it points.
- One chain at a time. A stopped background chain leaves its child script alive, and that script starts
  its next cell the moment the harness process is killed; list processes by command line
  (`Win32_Process`) and confirm `pg_stat_activity` is empty before starting another round.
- The `quick` preset's `throughput` cells are bimodal in every tree measured (the enqueue phase
  runs at about 480 jobs/s or at tens of thousands); read drain and enqueue for the verdict.
- `--seed-history N` measures the minute after a million-row bulk change, not a populated steady state,
  until the harness gets a settle step after seeding; report it as informational.
- Docker Desktop keeps its disk image on C: by default and applies a new location only through its own
  Settings dialog (Resources, Advanced, Disk image location); editing the settings file does nothing.

## Coverage

Published, never gated. The `build-test` CI job runs `tools/coverage.ps1`, which instruments
`tests/Acta.Tests` and `tests/Acta.Tests.Conformance.Sqlite` with coverlet and merges them into one
report, uploaded as the `coverage` artifact. There is no threshold and no percentage to fail on
purpose: a target invites tests written to colour lines rather than to falsify behaviour.

The deliverable is [the blind-spot list](../certification/coverage-baseline-rc1.md) — the recorded
baseline plus, for ten failure areas, which code paths nothing executes — and, per release round, a
baseline page beside it that records the new numbers with `tools/coverage.ps1` and says which
entries moved ([coverage-baseline-rc2.md](../certification/coverage-baseline-rc2.md) is the current
one); a blind spot that a new test closed should leave the list, and a new one should join it.

## Packaging

Each line names the evidence that asserts it; none is checked by hand.

- Package smoke: the `pack-smoke` CI job packs the shippable libraries and runs
  `tests/PackageSmoke/run.ps1` against those artifacts on every push, so each provider package is
  consume-proven self-contained (runtime, `[Job]` generator, analyzers) with no project references.
- Native AOT guardrail: the `aot-publish` CI job runs `NativeAotPublishTests`
  (`dotnet publish -p:PublishAot=true` on anvil/Anvil, asserting a clean native compile).
- Version/tag: MinVer derives the packed version from the `v*` tag itself, and `publish-nuget`
  pushes the `packages` artifact produced by that same tag run, so the published version cannot
  disagree with the tag. `run.ps1` additionally rejects a non-semver version string.
- NuGet metadata: the metadata gate in `tests/PackageSmoke/run.ps1` opens every packed nupkg and
  asserts description, Apache-2.0 license expression, repository URL, a packed readme, and a
  MinVer-shaped version.

## Docs and release notes

- README reviewed.
- Known limitations reviewed.
- Production guide reviewed.
- Contract evolution guidance reviewed.
- `docs/release-notes.md` carries a section for this version. Not "when applicable": a published tag
  with no notes leaves the upgrade path as a list of commit messages, which is how `0.6.0-beta.1` and
  `0.7.0-beta.1` both shipped. Lead with what a consumer must change, then everything else.
- Flip the section header from "(unreleased)" to "Tagged <date>" **before** tagging, in the last
  commit the tag points at. A tag is an immutable snapshot, so a header the tag captures as
  "(unreleased)" says that forever — `v1.0.0-rc.1` ships with exactly that wart, and the GitHub
  release links straight to it.
- Preview compatibility policy stated in known limitations still matches the release.

## After publishing

- Create the GitHub pre-release from the tag (`gh release create v<version> --verify-tag --prerelease`),
  titled with the bare version and carrying the notes' summary paragraph, the upgrade paragraph, the
  highlights, the evidence, and a link to the release-notes section at the tag, in the shape of the
  earlier releases. The packages publish without it; the release page is what a visitor reads.
- `dotnet nuget locals http-cache --clear` first. The local index is cached for roughly half an hour,
  so a restore straight after publishing resolves the previous version and the demo bump below silently
  verifies the wrong artifact.
- Bump the Acta `PackageVersion` entries in `demos/Directory.Packages.props` to the released version.
- Build both demos (`dotnet build demos/AcmeShop`, `dotnet build demos/ApiWorkerSplit`) and run one end
  to end. The demos consume the published packages, so this is the release verification that the packaged
  artifacts work in a real multi-project app: analyzers and the `[Job]` generator flowing from package
  assets, the dashboard serving from the embedded assets, providers resolving transitively.
- Review useacta.net for staleness before the site deploys, and only once the packages resolve from
  nuget.org, because the site's install line names the version a visitor will restore. Mandatory,
  not "when applicable": a release once shipped with the site still naming the previous candidate in two places. Run
  `node tools/site-check.mjs --version <released version>`; it fails when the install line names
  another version, when a nav or footer differs between pages, and on horizontal overflow at phone
  and tablet widths, and it lists every other version literal under `site/` for the hand read,
  since benchmark provenance may legitimately cite an earlier round. Then read the pages once by hand for what a script cannot
  judge: the benchmark strip cites the round the release notes cite, the concept count is current,
  and every claim about semantics still matches the guide.

## Final checks

- No generated docs drift.
- No generated migration snapshot drift.
- No baseline stamp drift: `Acta.Emit check` rehashes every provider's `M001` against its generated constant.
- No accidental dashboard build artifacts.
- No local benchmark output committed unless intentionally included.
- Public examples still match current package names and startup APIs.
