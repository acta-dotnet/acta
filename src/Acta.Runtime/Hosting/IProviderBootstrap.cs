namespace Acta.Runtime.Hosting;

/// <summary>Provider startup hook used to prepare durable infrastructure before workers initialize.</summary>
internal interface IProviderBootstrap
{
    Task RunAsync(CancellationToken ct);
}
