using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using static Acta.Generators.ActaManifestGenerator;
using static Acta.Generators.JobPolicyValues;

namespace Acta.Generators;

/// <summary>Reads a <c>[Job]</c> attribute's named arguments into the definition policy the descriptor carries.</summary>
internal static class JobPolicyReader
{
    /// <summary>
    /// Framework defaults: keep aligned with JobAttribute's defaults.
    /// </summary>
    internal const int DefaultMaxAttempts = 15;

    /// <summary>
    /// The declared concurrency limit's range, the same 1..1024 band JobsOptions allows for
    /// MaxConcurrentExecutors: a limit above the largest possible executor pool cannot bind.
    /// </summary>
    internal const int MinConcurrencyLimit = 1;
    internal const int MaxConcurrencyLimit = 1024;
    internal const string DefaultPriorityName = "Normal";
    internal const string DefaultEventRecordingName = "Audit";
    internal const string DefaultAlertProfileName = "OnFailure";
    internal const string DefaultFiringFailurePolicyName = "Continue";
    internal const string DefaultScheduledDeliveryName = "Permanent";

    internal static AttributePolicy ReadAttributePolicy(AttributeData attr, List<DiagnosticRecord> diagnostics, Location location)
    {
        var draft = new PolicyDraft();
        foreach (var named in attr.NamedArguments)
        {
            // Each key belongs to exactly one group, so the arguments are still read once each, in order.
            _ =
                ReadFormatArgument(named, draft)
                || ReadCodeArgument(named, draft, diagnostics, location)
                || ReadLimitArgument(named, draft, diagnostics, location)
                || ReadOperatorArgument(named, draft, diagnostics, location);
        }

        // ACTA0132: `Format` is the both-sides shorthand; pairing it with a per-side override is
        // ambiguous. The void-output and unknown-name variants are raised later, where the handler's
        // result shape and the validated format set are known.
        if (draft.Format is not null)
        {
            if (draft.InputFormat is not null)
            {
                diagnostics.Add(ManifestDiagnostics.PayloadFormatConflict("InputFormat", location));
            }
            if (draft.OutputFormat is not null)
            {
                diagnostics.Add(ManifestDiagnostics.PayloadFormatConflict("OutputFormat", location));
            }
        }

        return new AttributePolicy(
            draft.Format,
            draft.InputFormat,
            draft.OutputFormat,
            draft.PriorityName,
            draft.MaxAttempts,
            draft.ConcurrencyLimit,
            draft.RateLimit,
            draft.RateName,
            draft.Lane,
            draft.EventRecordingName,
            draft.AlertProfileName,
            draft.FiringFailurePolicyName,
            draft.ScheduledDeliveryName,
            draft.TenantRequirementId,
            draft.BackoffExpression,
            draft.ExecutionTimeoutSeconds,
            draft.DeadlineSeconds,
            draft.DeadlineBehaviorId,
            draft.JobRetentionSeconds,
            draft.AlertChannelName,
            draft.RunbookUrl,
            draft.DisplayName,
            draft.Description
        );
    }

    /// <summary>The policy as the named arguments fill it in, starting from the framework defaults.</summary>
    private sealed class PolicyDraft
    {
        public string? Format { get; set; }
        public string? InputFormat { get; set; }
        public string? OutputFormat { get; set; }
        public string PriorityName { get; set; } = DefaultPriorityName;
        public int MaxAttempts { get; set; } = DefaultMaxAttempts;
        public int? ConcurrencyLimit { get; set; }
        public string? RateLimit { get; set; }
        public string? RateName { get; set; }
        public string? Lane { get; set; }
        public string? EventRecordingName { get; set; }
        public string AlertProfileName { get; set; } = DefaultAlertProfileName;
        public string FiringFailurePolicyName { get; set; } = DefaultFiringFailurePolicyName;
        public string ScheduledDeliveryName { get; set; } = DefaultScheduledDeliveryName;
        public byte TenantRequirementId { get; set; }
        public string? BackoffExpression { get; set; }
        public int? ExecutionTimeoutSeconds { get; set; }
        public int? DeadlineSeconds { get; set; }
        public byte DeadlineBehaviorId { get; set; } = 10;
        public int? JobRetentionSeconds { get; set; }
        public string? AlertChannelName { get; set; }
        public string? RunbookUrl { get; set; }
        public string? DisplayName { get; set; }
        public string? Description { get; set; }
    }

    private static bool ReadFormatArgument(KeyValuePair<string, TypedConstant> named, PolicyDraft draft)
    {
        switch (named.Key)
        {
            case "Format":
                if (named.Value.Value is string fmt && !string.IsNullOrWhiteSpace(fmt))
                {
                    draft.Format = fmt;
                }
                return true;

            case "InputFormat":
                if (named.Value.Value is string ifmt && !string.IsNullOrWhiteSpace(ifmt))
                {
                    draft.InputFormat = ifmt;
                }
                return true;

            case "OutputFormat":
                if (named.Value.Value is string ofmt && !string.IsNullOrWhiteSpace(ofmt))
                {
                    draft.OutputFormat = ofmt;
                }
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// A code argument by its byte: the member's name, or the family's default with an ACTA0105 when the byte names no
    /// member; a value the reader cannot read as a byte leaves <paramref name="current"/> as it was.
    /// </summary>
    private static string ReadCode(
        KeyValuePair<string, TypedConstant> named,
        Func<byte, string?> idToName,
        string fallback,
        string requirement,
        string current,
        List<DiagnosticRecord> diagnostics,
        Location location
    )
    {
        if (!TryGetEnumByte(named.Value, out var id))
        {
            return current;
        }

        var name = idToName(id);
        if (name is null)
        {
            diagnostics.Add(
                ManifestDiagnostics.InvalidPolicyValue(
                    named.Key,
                    id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    requirement,
                    location
                )
            );
        }

        return name ?? fallback;
    }

    private static bool ReadCodeArgument(
        KeyValuePair<string, TypedConstant> named,
        PolicyDraft draft,
        List<DiagnosticRecord> diagnostics,
        Location location
    )
    {
        switch (named.Key)
        {
            case "Priority":
                draft.PriorityName = ReadCode(
                    named,
                    PriorityIdToName,
                    DefaultPriorityName,
                    "The value is not a defined `JobPriorityCode`.",
                    draft.PriorityName,
                    diagnostics,
                    location
                );
                return true;

            case "EventRecording":
                // Null until set: the writer tells an unset recording from one set to the default.
                if (TryGetEnumByte(named.Value, out _))
                {
                    draft.EventRecordingName = ReadCode(
                        named,
                        EventRecordingIdToName,
                        DefaultEventRecordingName,
                        "The value is not a defined `EventRecordingCode`.",
                        DefaultEventRecordingName,
                        diagnostics,
                        location
                    );
                }
                return true;

            case "TenantRequirement":
                if (TryGetEnumByte(named.Value, out var tr))
                {
                    if (TenantRequirementIdToName(tr) is null)
                    {
                        diagnostics.Add(
                            ManifestDiagnostics.InvalidPolicyValue(
                                named.Key,
                                tr.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                "The value is not a defined `DefinitionTenantRequirementCode`.",
                                location
                            )
                        );
                    }
                    else
                    {
                        draft.TenantRequirementId = tr;
                    }
                }
                return true;

            case "AlertProfile":
                draft.AlertProfileName = ReadCode(
                    named,
                    AlertProfileIdToName,
                    DefaultAlertProfileName,
                    "The value is not a defined `AlertProfileCode`.",
                    draft.AlertProfileName,
                    diagnostics,
                    location
                );
                return true;

            case "FiringFailurePolicy":
                draft.FiringFailurePolicyName = ReadCode(
                    named,
                    FiringFailurePolicyIdToName,
                    DefaultFiringFailurePolicyName,
                    "The policy is `FiringFailurePolicyCode.Continue` or `FiringFailurePolicyCode.PauseScheduling`.",
                    draft.FiringFailurePolicyName,
                    diagnostics,
                    location
                );
                return true;

            case "ScheduledDelivery":
                draft.ScheduledDeliveryName = ReadCode(
                    named,
                    ScheduledDeliveryIdToName,
                    DefaultScheduledDeliveryName,
                    "The delivery is `ScheduledDeliveryCode.Permanent` or `ScheduledDeliveryCode.PerOccurrence`.",
                    draft.ScheduledDeliveryName,
                    diagnostics,
                    location
                );
                return true;

            case "DeadlineBehavior":
                if (TryGetEnumByte(named.Value, out var db))
                {
                    draft.DeadlineBehaviorId = db;
                }
                return true;

            default:
                return false;
        }
    }

    private static bool ReadLimitArgument(
        KeyValuePair<string, TypedConstant> named,
        PolicyDraft draft,
        List<DiagnosticRecord> diagnostics,
        Location location
    )
    {
        switch (named.Key)
        {
            case "MaxAttempts":
                if (named.Value.Value is int m)
                {
                    if (m > 0)
                    {
                        draft.MaxAttempts = m;
                    }
                    else
                    {
                        diagnostics.Add(
                            ManifestDiagnostics.InvalidPolicyValue(
                                named.Key,
                                m.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                "The failure budget is at least 1.",
                                location
                            )
                        );
                    }
                }
                return true;

            case "ConcurrencyLimit":
                if (named.Value.Value is int cl)
                {
                    if (cl is >= MinConcurrencyLimit and <= MaxConcurrencyLimit)
                    {
                        draft.ConcurrencyLimit = cl;
                    }
                    else
                    {
                        diagnostics.Add(
                            ManifestDiagnostics.InvalidPolicyValue(
                                named.Key,
                                cl.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                $"The concurrency limit is between {MinConcurrencyLimit} and {MaxConcurrencyLimit}.",
                                location
                            )
                        );
                    }
                }
                return true;

            case "RateLimit":
                draft.RateLimit = ReadRateLimit(named, diagnostics, location);
                return true;

            case "Backoff":
                draft.BackoffExpression = ReadBackoff(named, diagnostics, location);
                return true;

            case "ExecutionTimeout":
                draft.ExecutionTimeoutSeconds = ReadDurationSeconds(named, diagnostics, location);
                return true;

            case "Deadline":
                draft.DeadlineSeconds = ReadDurationSeconds(named, diagnostics, location);
                return true;

            case "JobRetention":
                draft.JobRetentionSeconds = ReadDurationSeconds(named, diagnostics, location);
                return true;

            default:
                return false;
        }
    }

    private static bool ReadOperatorArgument(
        KeyValuePair<string, TypedConstant> named,
        PolicyDraft draft,
        List<DiagnosticRecord> diagnostics,
        Location location
    )
    {
        switch (named.Key)
        {
            case "RateName":
                if (named.Value.Value is string rk && !string.IsNullOrWhiteSpace(rk))
                {
                    if (KebabName.IsValid(rk, maxLength: 128, allowSystemPrefix: false))
                    {
                        draft.RateName = rk;
                    }
                    else
                    {
                        diagnostics.Add(
                            ManifestDiagnostics.InvalidPolicyValue(
                                named.Key,
                                $"\"{rk}\"",
                                "Rate names are lowercase kebab-case (`[a-z][a-z0-9-]*`), at most 128 chars.",
                                location
                            )
                        );
                    }
                }
                return true;

            case "Lane":
                if (named.Value.Value is string ln)
                {
                    if (TryCanonicalizeLane(ln, out var canonicalLane))
                    {
                        draft.Lane = canonicalLane;
                    }
                    else
                    {
                        diagnostics.Add(
                            ManifestDiagnostics.InvalidPolicyValue(
                                named.Key,
                                $"\"{ln}\"",
                                "Lanes are names: dotted kebab-case (`[a-z][a-z0-9-]*` segments joined by '.'), at most 128 chars after trimming and lowercasing, and not the reserved `sys` or under `sys.`.",
                                location
                            )
                        );
                    }
                }
                return true;

            case "AlertChannelName":
                if (named.Value.Value is string ch && !string.IsNullOrWhiteSpace(ch))
                {
                    if (KebabName.IsValid(ch, maxLength: 128, allowSystemPrefix: false))
                    {
                        draft.AlertChannelName = ch;
                    }
                    else
                    {
                        diagnostics.Add(
                            ManifestDiagnostics.InvalidPolicyValue(
                                named.Key,
                                $"\"{ch}\"",
                                "Alert channel names are lowercase kebab-case (`[a-z][a-z0-9-]*`), at most 128 chars.",
                                location
                            )
                        );
                    }
                }
                return true;

            case "RunbookUrl":
                if (named.Value.Value is string rb && !string.IsNullOrWhiteSpace(rb))
                {
                    draft.RunbookUrl = rb;
                }
                return true;

            case "DisplayName":
                if (named.Value.Value is string dn && !string.IsNullOrWhiteSpace(dn))
                {
                    draft.DisplayName = dn;
                }
                return true;

            case "Description":
                if (named.Value.Value is string desc && !string.IsNullOrWhiteSpace(desc))
                {
                    draft.Description = desc;
                }
                return true;

            default:
                return false;
        }
    }
}
