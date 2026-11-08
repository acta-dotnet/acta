using System.Globalization;
using System.Text.Json.Nodes;
using Acta.AspNetCore;
using Microsoft.OpenApi;

namespace Acta.Tests.Contracts;

// What the server's parsers and validators hold a request to beyond its types, as JSON Schema keywords. A pattern accepts
// exactly what its parser accepts, written over the raw value the parser is given: a name or key the parser trims and
// folds to lowercase matches with the white space around it and in any case. Each is ECMA-262 with every character
// outside printable ASCII escaped. OpenApiContractTests runs each parser against its pattern, and each bound against the
// route that holds it.
internal static partial class ActaOpenApiDocument
{
    /// <summary>The white space <c>string.Trim</c> removes: every character <c>char.IsWhiteSpace</c> answers true for.</summary>
    public const string TrimmedSpace = "[\\t-\\r \\u0085\\u00A0\\u1680\\u2000-\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000]";

    /// <summary>The white space an integer's parse skips on either side of it.</summary>
    private const string NumberSpace = "[\\t-\\r ]";

    // A name's characters before the fold to lowercase: ASCII letters in either case, digits, and the Kelvin sign, the one
    // other character whose lowercase is ASCII (k).
    private const string NameSegment = "[A-Za-z\\u212A](?:[-0-9A-Za-z\\u212A]*[0-9A-Za-z\\u212A])?";
    private const string NameCharacter = "[-.0-9A-Za-z\\u212A]";

    /// <summary>A job ref as every job route reads it: job_, then 32 hex digits in either case.</summary>
    public const string JobRefPattern = "^job_[0-9A-Fa-f]{32}$";

    /// <summary>
    /// A job ref, or the debug lookup a host with numeric lookup takes on its job reads: id: in either case, then a number
    /// above 0 with optional white space around it, a + sign, leading zeros, and trailing NUL characters, as a 64-bit
    /// integer parse takes them. A number past the 64-bit range matches and is refused.
    /// </summary>
    public const string JobTargetPattern =
        "^(?:job_[0-9A-Fa-f]{32}|[Ii][Dd]:" + NumberSpace + "*\\+?0*[1-9][0-9]*" + NumberSpace + "*\\u0000*)$";

    /// <summary>An alert ref: alr_, then 32 hex digits in either case.</summary>
    public const string AlertRefPattern = "^alr_[0-9A-Fa-f]{32}$";

    /// <summary>A worker ref: wrk_, then 32 hex digits in either case.</summary>
    public const string WorkerRefPattern = "^wrk_[0-9A-Fa-f]{32}$";

    /// <summary>Display text Acta keeps as given, such as a tag value: no control character.</summary>
    public const string DisplayValuePattern = "^[^\\u0000-\\u001F\\u007F-\\u009F]*$";

    /// <summary>Printable ASCII, the space included, as a runbook URL is.</summary>
    public const string PrintableAsciiPattern = "^[ -~]*$";

    /// <summary>
    /// A name as <c>NameSyntax.Canonicalize</c> takes it: dotted kebab once trimmed and lowercased, at most
    /// <paramref name="maxLength"/> characters when one is given, and neither sys nor under sys. when
    /// <paramref name="reserveSystem"/> is set.
    /// </summary>
    public static string NamePattern(int? maxLength = null, bool reserveSystem = false) =>
        "^"
        + TrimmedSpace
        + "*"
        + (maxLength is { } max ? $"(?={NameCharacter}{{1,{max}}}{TrimmedSpace}*$)" : "")
        + (reserveSystem ? $"(?![Ss][Yy][Ss](?:\\.|{TrimmedSpace}*$))" : "")
        + NameSegment
        + "(?:\\."
        + NameSegment
        + ")*"
        + TrimmedSpace
        + "*$";

    /// <summary>
    /// A key as <c>KeySyntax.Canonicalize</c> takes it: once trimmed, 1 to <paramref name="maxLength"/> printable ASCII
    /// characters without white space, and with <paramref name="reserveSystem"/> not starting sys. in any case, as a
    /// deduplication key at enqueue.
    /// </summary>
    public static string KeyPattern(int maxLength, bool reserveSystem = false) =>
        "^" + TrimmedSpace + "*" + (reserveSystem ? "(?![Ss][Yy][Ss]\\.)" : "") + $"[!-~]{{1,{maxLength}}}" + TrimmedSpace + "*$";

    /// <summary>
    /// One rule a handler or validator holds a request member to beyond its type, written into the member's schema:
    /// <c>minimum</c>, <c>maximum</c>, <c>maxLength</c>, <c>maxItems</c>, and <c>pattern</c> as given.
    /// </summary>
    public sealed record MemberRule(
        string Schema,
        string Member,
        int? Minimum = null,
        int? Maximum = null,
        int? MaxLength = null,
        int? MaxItems = null,
        string? Pattern = null
    );

    /// <summary>The request members' rules; <c>OpenApiContractTests</c> proves each against the route that holds it.</summary>
    public static readonly MemberRule[] RequestMemberRules =
    [
        new("EnqueueJobRequest", "jobNamespace", Pattern: NamePattern(NameSyntax.DefaultMaxLength, reserveSystem: true)),
        new("EnqueueJobRequest", "jobName", Pattern: NamePattern(NameSyntax.ExtendedMaxLength, reserveSystem: true)),
        new("EnqueueJobRequest", "deduplicationKey", Pattern: KeyPattern(DeduplicationKey.MaxLength, reserveSystem: true)),
        new("EnqueueJobRequest", "correlationKey", Pattern: KeyPattern(NameSyntax.DefaultMaxLength)),
        new("EnqueueJobRequest", "tenantName", Pattern: NamePattern(NameSyntax.ExtendedMaxLength, reserveSystem: true)),
        new("EnqueueJobRequest", "delaySeconds", Minimum: 0),
        new("EnqueueJobRequest", "lane", Pattern: NamePattern(NameSyntax.ExtendedMaxLength, reserveSystem: true)),
        new("EnqueueJobRequest", "concurrencyKey", Pattern: KeyPattern(NameSyntax.ExtendedMaxLength)),
        new("EnqueueJobRequest", "tags", MaxItems: TagLimits.MaxTagsPerTarget),
        new("TagInput", "name", Pattern: NamePattern(NameSyntax.ExtendedMaxLength, reserveSystem: true)),
        new("TagInput", "value", MaxLength: NameSyntax.ExtendedMaxLength, Pattern: DisplayValuePattern),
        new("UpsertTagRequest", "value", MaxLength: NameSyntax.ExtendedMaxLength, Pattern: DisplayValuePattern),
        new("RegisterTenantRequest", "tenantName", Pattern: NamePattern(NameSyntax.ExtendedMaxLength, reserveSystem: true)),
        new("RegisterTenantRequest", "displayName", MaxLength: TextLimits.TenantDisplayName),
        new("RegisterTenantRequest", "description", MaxLength: TextLimits.TenantDescription),
        new("UpdateTenantRequest", "displayName", MaxLength: TextLimits.TenantDisplayName),
        new("UpdateTenantRequest", "description", MaxLength: TextLimits.TenantDescription),
        new("UpdateNamespaceRequest", "ownerTeam", MaxLength: TextLimits.NamespaceOwnerTeam),
        new("UpdateNamespaceRequest", "description", MaxLength: TextLimits.NamespaceDescription),
        new("DefinitionPolicyOverrides", "maxAttempts", Minimum: 1, Maximum: JobDefinitionRegistration.MaxMaxAttempts),
        new(
            "DefinitionPolicyOverrides",
            "concurrencyLimit",
            Minimum: JobDefinitionRegistration.MinConcurrencyLimit,
            Maximum: JobDefinitionRegistration.MaxConcurrencyLimit
        ),
        new("DefinitionPolicyOverrides", "backoff", MaxLength: ActaTextLimits.DefinitionBackoff),
        new(
            "DefinitionPolicyOverrides",
            "executionTimeoutSeconds",
            Minimum: 1,
            Maximum: JobDefinitionRegistration.MaxExecutionTimeoutSeconds
        ),
        new("DefinitionPolicyOverrides", "deadlineSeconds", Minimum: 0),
        new("DefinitionPolicyOverrides", "jobRetentionSeconds", Minimum: 0),
        new("DefinitionPolicyOverrides", "alertChannelName", Pattern: NamePattern(ActaTextLimits.AlertChannelName)),
        new("DefinitionPolicyOverrides", "runbookUrl", MaxLength: ActaTextLimits.DefinitionRunbookUrl, Pattern: PrintableAsciiPattern),
        new("PublishCalendarRequest", "candidateVersion", Minimum: 1),
    ];

    /// <summary>The members of the enqueue that name when the job first runs, of which a request sends at most one.</summary>
    public static readonly string[] EnqueueTimingMembers = ["delaySeconds", "nextRunAtUtc"];

    /// <summary>
    /// The pattern of a path parameter: a ref's for a ref, the debug lookup beside the job ref on a route that takes it, and
    /// the name syntax for every name under its kind's cap, which every route holds it to: a namespace's 64 characters, and
    /// 128 for every other name. A key with no cap here fails the generation, so a new name states its own.
    /// </summary>
    public static string PathPattern(string parameter, bool takesNumericJobId) =>
        parameter switch
        {
            "jobRef" => takesNumericJobId ? JobTargetPattern : JobRefPattern,
            "alertRef" => AlertRefPattern,
            "workerRef" => WorkerRefPattern,
            "jobNamespace" => NamePattern(NameSyntax.DefaultMaxLength),
            "jobName" or "scheduleName" or "calendarName" or "tenantName" or "tagName" or "signalName" => NamePattern(
                NameSyntax.ExtendedMaxLength
            ),
            _ => throw new InvalidOperationException($"The path parameter {parameter} states no pattern."),
        };

    /// <summary>
    /// The pattern of a query parameter that filters by a name, null for every other: a namespace under its 64 characters,
    /// a job name, tenant, or lane under 128, and the name fragment the namespaces, definitions, and calendars lists match,
    /// a name's characters around which white space is trimmed, under the cap of the name it finds. The query binding reads
    /// an empty value as an omitted one, which an optional filter takes and a required one refuses, and the tenant, lane,
    /// and fragment filters read white space alone as no filter.
    /// </summary>
    public static string? QueryPattern(string parameter, string? route, bool required)
    {
        var empty = required ? "" : "^$|";
        return parameter switch
        {
            "jobNamespace" => empty + NamePattern(NameSyntax.DefaultMaxLength),
            "jobName" => empty + NamePattern(NameSyntax.ExtendedMaxLength),
            "tenantName" or "lane" => "^" + TrimmedSpace + "*$|" + NamePattern(NameSyntax.ExtendedMaxLength),
            "nameContains" => route?.TrimEnd('/').Split('/')[^1] switch
            {
                "namespaces" => FragmentPattern(NameSyntax.DefaultMaxLength),
                "definitions" or "calendars" => FragmentPattern(NameSyntax.ExtendedMaxLength),
                _ => null,
            },
            _ => null,
        };
    }

    private static string FragmentPattern(int maxLength) =>
        "^" + TrimmedSpace + "*" + NameCharacter + "{0," + maxLength.ToString(CultureInfo.InvariantCulture) + "}" + TrimmedSpace + "*$";

    /// <summary>The job ref's description on a route that also takes the debug lookup.</summary>
    private static string NumericJobRefDescription =>
        RouteKeys.Descriptions["jobRef"].TrimEnd('.')
        + "; where the host enables numeric lookup, id: and the job's number read the job as well.";

    // Gives every path parameter its pattern and an example, the value the examples' requests send.
    private static void DescribePathParameter(OpenApiParameter parameter, bool takesNumericJobId)
    {
        if (parameter.Schema is not OpenApiSchema schema)
        {
            return;
        }
        schema.Pattern = PathPattern(parameter.Name!, takesNumericJobId);
        parameter.Example = JsonValue.Create(OpenApiExamples.RouteValues[parameter.Name!]);
        if (takesNumericJobId && parameter.Name == "jobRef")
        {
            parameter.Description = NumericJobRefDescription;
        }
    }

    // Writes each member rule into its schema, and the enqueue's one-timing rule as a not over both timing members.
    private static void ApplyRequestRules(IDictionary<string, IOpenApiSchema> schemas)
    {
        foreach (var rule in RequestMemberRules)
        {
            if (
                !schemas.TryGetValue(rule.Schema, out var entry)
                || entry is not OpenApiSchema { Properties: { } properties }
                || !properties.TryGetValue(rule.Member, out var member)
                || member is not OpenApiSchema schema
            )
            {
                throw new InvalidOperationException($"{rule.Schema}.{rule.Member} carries a rule but is no inline member of the document.");
            }

            schema.Minimum = rule.Minimum?.ToString(CultureInfo.InvariantCulture);
            schema.Maximum = rule.Maximum?.ToString(CultureInfo.InvariantCulture);
            schema.MaxLength = rule.MaxLength;
            schema.MaxItems = rule.MaxItems;
            schema.Pattern = rule.Pattern;
        }

        if (schemas.TryGetValue("EnqueueJobRequest", out var enqueue) && enqueue is OpenApiSchema request)
        {
            // Present with null counts as left out, so the rule refuses both only when neither is null.
            request.Not = new OpenApiSchema
            {
                Required = EnqueueTimingMembers.ToHashSet(StringComparer.Ordinal),
                Properties = EnqueueTimingMembers.ToDictionary(
                    member => member,
                    IOpenApiSchema (_) => new OpenApiSchema { Not = new OpenApiSchema { Type = JsonSchemaType.Null } },
                    StringComparer.Ordinal
                ),
            };
        }
    }
}
