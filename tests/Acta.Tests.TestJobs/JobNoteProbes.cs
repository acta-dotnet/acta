using Acta;

namespace TestJobs;

/// <summary>Probes for <c>ctx.NoteAsync</c>: the one event code an application can write.</summary>
public static class JobNoteProbes
{
    public sealed record NoteDetail(string Stage, int Attempt);

    [Job("job-note")]
    public static async Task WriteNotes(JobContext ctx, CancellationToken ct)
    {
        await ctx.NoteAsync("plain note", ct);
        await ctx.NoteAsync("note with detail", new NoteDetail("gather", 1), ct);
    }
}
