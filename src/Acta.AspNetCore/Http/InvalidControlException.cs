namespace Acta.AspNetCore;

/// <summary>A verb's own check on its body failed: a 400 under the verb's title, with this message as the detail.</summary>
internal sealed class InvalidControlException(string title, string detail) : Exception(detail)
{
    public string Title { get; } = title;
}
