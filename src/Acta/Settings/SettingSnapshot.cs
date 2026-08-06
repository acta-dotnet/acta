namespace Acta;

/// <summary>
/// One global setting row as read through <see cref="ISettings.GetAsync"/>: the name-addressed
/// value with its operator description and concurrency version.
/// </summary>
/// <param name="Name">The lowercase dotted-kebab setting name (for example <c>billing.retry-budget</c>).</param>
/// <param name="Value">The stored value decoded as text; null when the row carries no body.</param>
/// <param name="Description">Operator-readable description of what the setting controls.</param>
/// <param name="CreatedAtUtc">When the setting row was first inserted.</param>
/// <param name="ModifiedAtUtc">Last-write instant; every set bumps it.</param>
/// <param name="Version">Optimistic-concurrency token; every set increments it.</param>
public sealed record SettingSnapshot(
    string Name,
    string? Value,
    string? Description,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    int Version
);
