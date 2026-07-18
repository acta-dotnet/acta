using Acta.Features.Jobs;
using Xunit;

namespace Acta.Tests.Jobs;

public class JobControlActorTests
{
    [Fact]
    public void SanitizeActorKey_folds_non_ascii_characters_to_question_marks()
    {
        Assert.Equal("?iga Novak", JobControlActor.SanitizeActorKey("Žiga Novak"));
    }

    [Fact]
    public void SanitizeActorKey_returns_null_for_null_or_whitespace()
    {
        Assert.Null(JobControlActor.SanitizeActorKey(null));
        Assert.Null(JobControlActor.SanitizeActorKey("   "));
    }

    [Fact]
    public void SanitizeActorKey_truncates_over_length_ascii_input()
    {
        var input = new string('a', 200);

        var sanitized = JobControlActor.SanitizeActorKey(input);

        Assert.Equal(128, sanitized!.Length);
        Assert.Equal(new string('a', 128), sanitized);
    }
}
