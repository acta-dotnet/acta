namespace Acta;

/// <summary>
/// Which CRUD shape an <c>access_job_checkpoint</c> call takes. The values are the routine's dispatch
/// discriminator; they are never persisted.
/// </summary>
internal enum AccessJobCheckpointAction : short
{
    Set = 10,
    Get = 20,
    GetOrSet = 30,
    Exists = 40,
    Delete = 50,
}
