namespace Acta;

/// <summary>
/// Resolved contract route: namespace + job name + input format, plus the descriptor's input and
/// result CLR types so the call overloads can validate a hand-built
/// <see cref="JobCall{TInput}"/> against the registered job. The result format is not stored here: it
/// is read back from the stored payload at result time.
/// </summary>
internal readonly record struct JobContractRoute(
    string Namespace,
    string JobName,
    JobPayloadFormat InputFormat,
    Type InputType,
    Type? ResultType
);
