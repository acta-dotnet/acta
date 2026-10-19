namespace Acta.Emit.Shared.Model;

/// <summary>One discovered code family and its persisted values.</summary>
internal sealed record CodeFamilyModel(
    string Name,
    string CodeKind,
    IReadOnlyList<CodeEntryModel> Values,
    IReadOnlyList<ReservedCodeModel> ReservedCodes,
    IReadOnlyList<ReservedCodeRangeModel> ReservedRanges
);
