namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// The test jobs a spec deploys into its namespace, by name. They are the spec's whole catalog: a spec that names none
/// deploys none, and a job it enqueues or reads without naming it here is not there. <see cref="JobsOf{TSpec}"/> reads it.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DeploysAttribute(params string[] jobNames) : Attribute
{
    public IReadOnlyList<string> JobNames { get; } = jobNames;
}
