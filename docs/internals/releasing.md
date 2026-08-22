# Releasing

## Purpose

Checklist for preparing an Acta release. This is a manual process checklist; the only automated step
is publishing: pushing a `v*` tag makes CI pack and, once all jobs are green, push the packages to
nuget.org via Trusted Publishing (`publish-nuget` job in `ci.yml`, gated on the `release` environment).

## Build and test

- `dotnet restore Acta.slnx`
- `dotnet build Acta.slnx -c Release /p:ActaDashboardSkipNpm=true`
- `dotnet test tests/Acta.Tests/Acta.Tests.csproj -c Release`
- Full provider tests with Docker.
- Dashboard `npm ci`, `npm test`, `npm run build`.
- `dotnet run --project tools/Acta.Emit -- check`
- `dotnet csharpier check .`
- Schema guard, pre-1.0: a re-cut `M001` is allowed. When the release diff contains one, check that
  the baseline stamp was bumped in both `SqlDdlDialect.BaselineStamp` and
  `SchemaMigrationRunner.RequiredBaselineStamp`, and call out the required reprovision in the release
  notes.
- Schema/code-freeze guard, from 1.0.0: the release diff contains no M001 edits and no destructive
  migration statements, renumbered code pairs, retired-id reuse, or closed-family `255` assignments;
  schema changes ship only as additive `Mnnn` migrations.

## Frozen contracts

Four baselines fail a test on drift, so the suite above already catches an accidental change. What a
release adds is the judgement the test cannot make: **read each moved baseline and confirm the move was
intended.** A regenerated baseline is indistinguishable from a deliberate one once committed.

| Contract | Baseline | Regenerate |
|---|---|---|
| .NET public surface | `tests/Acta.Tests/Contracts/PublicApiSurface.approved.txt` | `ACTA_EMIT_API=1 dotnet test tests/Acta.Tests --filter PublicApiContractTests` |
| HTTP surface | `docs/reference/openapi.json` | `ACTA_EMIT_OPENAPI=1 dotnet test tests/Acta.Tests --filter OpenApiContractTests` |
| Persisted codes | hash in `PersistedCodeContractTests` | re-pin the hash by hand |
| Conformance docs | `docs/reference/conformance-contracts.md` | `ACTA_EMIT_DOCS=1 dotnet test tests/Acta.Tests --filter DocsContractTests` |

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

## Coverage

Published, never gated. The `build-test` CI job runs `tools/coverage.ps1`, which instruments
`tests/Acta.Tests` and `tests/Acta.Tests.Conformance.Sqlite` with coverlet and merges them into one
report, uploaded as the `coverage` artifact. There is no threshold and no percentage to fail on
purpose: a target invites tests written to colour lines rather than to falsify behaviour.

The deliverable is [the blind-spot list](../certification/coverage-baseline-rc1.md) — the recorded
baseline plus, for ten failure areas, which code paths nothing executes. Re-read it per release
round and update the numbers with `tools/coverage.ps1`; a blind spot that a new test closed should
leave the list, and a new one should join it.

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
- Preview compatibility policy stated in known limitations still matches the release.

## After publishing

- `dotnet nuget locals http-cache --clear` first. The local index is cached for roughly half an hour,
  so a restore straight after publishing resolves the previous version and the demo bump below silently
  verifies the wrong artifact.
- Bump the Acta `PackageVersion` entries in `demos/Directory.Packages.props` to the released version.
- Build both demos (`dotnet build demos/AcmeShop`, `dotnet build demos/ApiWorkerSplit`) and run one end
  to end. The demos consume the published packages, so this is the release verification that the packaged
  artifacts work in a real multi-project app: analyzers and the `[Job]` generator flowing from package
  assets, the dashboard serving from the embedded assets, providers resolving transitively.

## Final checks

- No generated docs drift.
- No generated migration snapshot drift.
- No accidental dashboard build artifacts.
- No local benchmark output committed unless intentionally included.
- Public examples still match current package names and startup APIs.
