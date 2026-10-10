namespace Acta;

/// <summary>
/// The contract-identifying columns of a definition, normalized the same way for the stored row and
/// an incoming descriptor so an unchanged restart never reads as drift.
/// </summary>
internal readonly record struct DefinitionContract(
    string InputTypeName,
    string? OutputTypeName,
    byte InputFormatId,
    string InputFormatName,
    byte OutputFormatId,
    string OutputFormatName
);
