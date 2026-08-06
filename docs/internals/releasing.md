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
- Release notes/changelog reviewed when applicable.
- Preview compatibility policy stated in known limitations still matches the release.

## After publishing

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
