namespace Acta;

/// <summary>Every definition in a namespace with its stored contract and effective policy.</summary>
internal sealed record GetDefinitionContractsResponse(IReadOnlyList<StoredDefinitionContract> Contracts);
