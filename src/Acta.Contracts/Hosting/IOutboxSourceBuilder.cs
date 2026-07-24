namespace Acta;

/// <summary>
/// Provider-neutral configuration surface for one external-outbox source, passed to
/// <see cref="IWorkerBuilder.AddOutboxRelay(string, System.Action{IOutboxSourceBuilder})"/>. Provider
/// packages extend this with <c>source.UsePostgres(...)</c>, <c>source.UseSqlServer(...)</c>, or
/// <c>source.UseSqlite(...)</c>, each of which records the single source store factory. Exactly one
/// provider selection is required; the seam never reuses or mutates the Acta-ledger provider
/// registration, and nothing here connects to the source at startup.
/// </summary>
public interface IOutboxSourceBuilder
{
    /// <summary>The canonical source name declared on <c>AddOutboxRelay</c>.</summary>
    string SourceName { get; }

    /// <summary>Optional source-schema override; when null the provider default applies.</summary>
    string? Schema { get; set; }

    /// <summary>Optional source-table override; when null the canonical <c>acta_outbox</c> name applies.</summary>
    string? Table { get; set; }

    /// <summary>
    /// Row-rejection count at which a recoverable failure quarantines. Default five; malformed and
    /// target-oversize rows quarantine immediately regardless of this value.
    /// </summary>
    int QuarantineThreshold { get; set; }
}
