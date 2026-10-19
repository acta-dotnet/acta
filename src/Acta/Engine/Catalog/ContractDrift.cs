namespace Acta;

/// <summary>
/// One eligible contract change found at registration: a definition whose stored generation is at
/// or below the incoming generation and whose contract columns differ from the incoming descriptor.
/// </summary>
internal sealed record ContractDrift(string JobName, DefinitionContract Stored, DefinitionContract Incoming);
