namespace Acta.Tests.Conformance.Contracts;

/// <summary>
/// The contract a conformance spec proves, in one sentence. The executable guarantees live on each test
/// method's <c>[Fact(DisplayName = "...")]</c>, not on this attribute.
/// </summary>
/// <remarks>
/// Create a contract descriptor.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ConformanceSpecAttribute(string id, string title) : Attribute
{
    /// <summary>
    /// Globally-unique stable id, e.g. <c>get-job.returns-snapshot</c>. Internal key only (uniqueness,
    /// doc ordering); not emitted into the generated document.
    /// </summary>
    public string Id { get; } = id;

    /// <summary>One-line human title.</summary>
    public string Title { get; } = title;

    /// <summary>Docs grouping bucket, e.g. <c>Execution</c>, <c>Variables</c>, <c>Alerts</c>.</summary>
    public string Area { get; set; } = "";

    /// <summary>The general invariant in one sentence, business meaning, no implementation detail.</summary>
    public string Contract { get; set; } = "";
}
