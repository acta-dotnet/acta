namespace Acta;

/// <summary>
/// Optimistic-concurrency token. The property must be of type <c>int</c> and named
/// `Version` by convention; SPs manually increment via <c>SET version = version + 1</c> on every UPDATE.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
internal sealed class DbConcurrencyTokenAttribute : Attribute { }
