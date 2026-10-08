namespace Acta.Runtime;

/// <summary>
/// The bounds one <c>sys.alerts</c> generate pass drains within: how many events one batch reads
/// (<c>BatchSize</c>), how many batches the pass may complete (<c>MaxBatches</c>), and the elapsed
/// budget it spends (<c>TimeBudget</c>). The time budget is cooperative - read between batches, never
/// inside one - so the batch in flight always finishes and always checkpoints its cursor. The first
/// bound reached ends the pass.
/// </summary>
internal sealed record AlertDrainBudget(int BatchSize, int MaxBatches, TimeSpan TimeBudget);
