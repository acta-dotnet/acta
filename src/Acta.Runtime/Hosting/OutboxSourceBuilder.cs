using Acta.Features.Outbox;

namespace Acta;

/// <summary>
/// Default <see cref="IOutboxSourceBuilder"/> implementation. Collects one source's overrides and its
/// single provider store factory for <see cref="WorkerBuilder.AddOutboxRelay"/> to validate and fold into
/// the worker's relay registration. Never instantiated by consumer code.
/// </summary>
internal sealed class OutboxSourceBuilder(string sourceName) : IOutboxSourceBuilder
{
    public string SourceName { get; } = sourceName;

    public string? Schema { get; set; }

    public string? Table { get; set; }

    public int QuarantineThreshold { get; set; } = 5;

    /// <summary>The single source-provider store factory, set by a provider extension via <see cref="SetStoreFactory"/>.</summary>
    internal IOutboxSourceStoreFactory? StoreFactory { get; private set; }

    /// <summary>Records the single source-provider store factory (called by a provider extension's
    /// <c>UseXxx</c>). Selecting a second provider is a configuration error.</summary>
    internal void SetStoreFactory(IOutboxSourceStoreFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (StoreFactory is not null)
        {
            throw new InvalidOperationException(
                $"Outbox relay source '{SourceName}' selects more than one provider. Call exactly one of "
                    + "source.UsePostgres/UseSqlServer/UseSqlite."
            );
        }

        StoreFactory = factory;
    }
}
