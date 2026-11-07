namespace Acta;

/// <summary>
/// One app version that has deployed its jobs in a namespace, one row per <c>(NamespaceId, DeploymentVersion)</c>,
/// written the first time that version's deploy lands there and never updated or purged. A version that has a row and
/// is not the namespace's current one is an older build, whose deploy changes nothing unless it is forced.
/// </summary>
[DbTable("deployments")]
[DbPrimaryKey(Name = "pk_deployments", Columns = ["namespace_id", "deployment_version"])]
internal sealed class DeploymentEntity : IEntity
{
    /// <summary>
    /// The namespace the version deployed in. The key's first column.
    /// </summary>
    [DbColumn("namespace_id", DbKind.Int32)]
    public int NamespaceId { get; init; }

    /// <summary>
    /// The app's version, as <c>JobsOptions.DeploymentVersion</c> names it, compared exactly: versions that differ
    /// only in case are two versions. The key's second column.
    /// </summary>
    [DbColumn("deployment_version", DbKind.AsciiString, Size = 128, CaseExact = true)]
    public string DeploymentVersion { get; init; } = default!;

    /// <summary>When the version first deployed in the namespace. Set server-side.</summary>
    [DbColumn("created_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime CreatedAtUtc { get; set; }
}
