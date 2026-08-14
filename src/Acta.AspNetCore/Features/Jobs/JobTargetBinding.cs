namespace Acta.AspNetCore.Features.Jobs;

/// <summary>
/// Parses a ref-addressed route target into a <see cref="JobLookup"/>. A <c>job_...</c> value is
/// always accepted as a <see cref="JobRef"/>; an <c>id:&lt;n&gt;</c> value resolves to the internal
/// numeric id only when the host set <see cref="ActaEndpointOptions.EnableNumericIdLookup"/>.
/// Anything else (including a bare integer) returns false, which callers map to 404.
/// </summary>
internal static class JobTargetBinding
{
    public const string IdPrefix = "id:";

    public static bool TryParseTarget(string target, ActaEndpointOptions options, out JobLookup job)
    {
        if (JobRef.TryParse(target, out var jobRef))
        {
            job = JobLookup.ByRef(jobRef);
            return true;
        }

        if (
            options.EnableNumericIdLookup
            && target.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase)
            && long.TryParse(target.AsSpan(IdPrefix.Length), out var id)
            && id > 0
        )
        {
            job = JobLookup.ById(id);
            return true;
        }

        job = default;
        return false;
    }
}
