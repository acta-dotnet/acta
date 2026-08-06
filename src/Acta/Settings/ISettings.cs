namespace Acta;

/// <summary>
/// Durable settings domain: slow-changing operator/deployment configuration in the central
/// <c>settings</c> table, addressed by name at one of three scopes. Reached through
/// <see cref="IActaOperations.Settings"/>. The scope is inferred from the optional target
/// arguments: neither given is deployment-wide (global), <c>namespaceName</c> alone narrows to one
/// namespace, and <c>namespaceName</c> + <c>jobName</c> narrows to one job definition. Get and set
/// only; reads are exact-scope (no fallback resolution). Because both operations are name-addressed
/// rows rather than columns, a newer Acta version can read and write a setting name older versions
/// never knew, with no migration.
/// </summary>
public interface ISettings
{
    /// <summary>Point-read one setting by name at the inferred scope; null when it has never been
    /// written. An unregistered scope target also reads as null: reads never report NotFound.</summary>
    ValueTask<SettingSnapshot?> GetAsync(string name, string? namespaceName = null, string? jobName = null, CancellationToken ct = default);

    /// <summary>
    /// Write one setting by name at the inferred scope: created when absent, overwritten when
    /// present (last write wins), with a version bump either way. NotFound when the scope target
    /// (namespace or definition) is not registered. Emits <c>setting.updated</c> with the setting
    /// name as evidence. The value is stored as text; callers needing structure store JSON text.
    /// Throws <see cref="PayloadTooLargeException"/> when the value exceeds
    /// <see cref="JobsOptions.MaxInlinePayloadBytes"/>.
    /// </summary>
    ValueTask<AdminControlResult> SetAsync(
        string name,
        string value,
        string? description = null,
        string? namespaceName = null,
        string? jobName = null,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );
}
