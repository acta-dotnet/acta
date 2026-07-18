# Releasing

## Purpose

Checklist for preparing an Acta release. This is a manual process checklist, not CI automation.

## Build and test

- `dotnet restore Acta.slnx`
- `dotnet build Acta.slnx -c Release /p:ActaDashboardSkipNpm=true`
- `dotnet test tests/Acta.Tests/Acta.Tests.csproj -c Release`
- Full provider tests with Docker.
- Dashboard `npm ci`, `npm test`, `npm run build`.
- `dotnet run --project tools/Acta.Emit -- check`
- `dotnet csharpier check .`
- Schema/code-freeze guard: the release diff contains no M001 edits and no destructive migration
  statements, renumbered code pairs, retired-id reuse, or closed-family `255` assignments; schema
  changes ship only as additive `Mnnn` migrations.

## Packaging

- Package smoke.
- Native AOT guardrail when enabled.
- Version/tag checked.
- NuGet metadata checked.

## Docs and release notes

- README reviewed.
- Known limitations reviewed.
- Production guide reviewed.
- Contract evolution guidance reviewed.
- Release notes/changelog reviewed when applicable.
- Preview compatibility policy stated in known limitations still matches the release.

## Final checks

- No generated docs drift.
- No generated migration snapshot drift.
- No accidental dashboard build artifacts.
- No local benchmark output committed unless intentionally included.
- Public examples still match current package names and startup APIs.
