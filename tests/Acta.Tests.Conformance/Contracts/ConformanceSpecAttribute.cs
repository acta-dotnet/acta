namespace Acta.Tests.Conformance.Contracts;

/// <summary>
/// The human-absorbable contract a conformance spec proves. Contract states the general invariant;
/// Arrange/Act/Assert tell the one concrete story the spec runs. The executable guarantees live on
/// each test method's <c>[Fact(DisplayName = "...")]</c>, while store coverage lives on
/// <c>[CoversStoreMethod]</c>, not on this attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ConformanceSpecAttribute : Attribute
{
    /// <summary>
    /// Create a contract descriptor.
    /// </summary>
    public ConformanceSpecAttribute(string id, string title)
    {
        Id = id;
        Title = title;
    }

    /// <summary>
    /// Globally-unique stable id, e.g. <c>get-job.returns-snapshot</c>. Internal key only (uniqueness,
    /// doc ordering); not emitted into the generated document.
    /// </summary>
    public string Id { get; }

    /// <summary>One-line human title.</summary>
    public string Title { get; }

    /// <summary>Docs grouping bucket, e.g. <c>Execution</c>, <c>Variables</c>, <c>Alerts</c>.</summary>
    public string Area { get; set; } = "";

    /// <summary>The general invariant in one sentence, business meaning, no implementation detail.</summary>
    public string Contract { get; set; } = "";

    /// <summary>One sentence of setup/configuration only: what exists before the spec acts.</summary>
    public string Arrange { get; set; } = "";

    /// <summary>One sentence naming the operation/runtime path the spec exercises.</summary>
    public string Act { get; set; } = "";

    /// <summary>One sentence stating the primary expected outcome.</summary>
    public string Assert { get; set; } = "";
}
