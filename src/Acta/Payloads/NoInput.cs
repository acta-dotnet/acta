namespace Acta;

/// <summary>
/// Framework sentinel occupying the input slot of a zero-input handler (<c>Handle()</c>,
/// <c>Handle(CancellationToken)</c>, <c>Handle(JobContext, CancellationToken)</c>). Handler authors
/// never write it; the generator places it in the descriptor's <c>InputType</c> so payload-less jobs
/// keep the non-null "one input type" shape, and it always maps to <see cref="JobPayloadFormat.None"/>.
/// </summary>
public readonly record struct NoInput;
