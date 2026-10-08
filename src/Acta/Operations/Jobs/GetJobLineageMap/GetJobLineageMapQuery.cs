namespace Acta;

/// <summary>The job whose lineage to map, and how many children to show.</summary>
internal sealed record GetJobLineageMapQuery(JobLookup Job, GetJobLineageMapOptions? Options);
