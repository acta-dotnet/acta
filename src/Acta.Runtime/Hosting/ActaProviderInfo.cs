namespace Acta.Runtime;

/// <summary>Provider-neutral runtime facts supplied by the selected durable provider package.</summary>
internal sealed record ActaProviderInfo(DbProvider Provider);
