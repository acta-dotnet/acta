using Acta;

namespace TestJobs;

public sealed record AddNumbers(int Left, int Right);

public sealed record AddNumbersResult(int Sum);

public static class AddNumbersHandler
{
    [Job("add-numbers")]
    public static AddNumbersResult Run(AddNumbers input) => new(input.Left + input.Right);
}
