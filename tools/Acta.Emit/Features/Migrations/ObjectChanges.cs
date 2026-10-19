namespace Acta.Emit.Features.Migrations;

/// <summary>One dialect's routines and views whose body is new or changed, and those gone from the source SQL.</summary>
internal sealed record ObjectChanges(IReadOnlyList<string> Changed, IReadOnlyList<string> Removed);
