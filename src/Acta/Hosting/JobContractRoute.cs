namespace Acta;

/// <summary>
/// Resolved contract route: namespace + job name + input format, plus the descriptor's input and
/// output CLR types so the call overloads can validate a hand-built
/// <see cref="JobCall{TInput}"/> against the registered job. Output format is not stored (the
/// result format is read back from the stored payload at result time).
/// </summary>
internal readonly record struct JobContractRoute(
    string Namespace,
    string JobName,
    JobPayloadFormat InputFormat,
    Type InputType,
    Type? OutputType
);
