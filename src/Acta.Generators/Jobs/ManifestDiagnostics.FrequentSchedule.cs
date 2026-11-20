using System.Globalization;
using Microsoft.CodeAnalysis;
using static Acta.Generators.ActaManifestGenerator;

namespace Acta.Generators;

/// <summary>ACTA0108, a schedule faster than once a minute on a job that does not state what Acta keeps of it.</summary>
internal static partial class ManifestDiagnostics
{
    internal static readonly DiagnosticDescriptor FrequentSchedule = new(
        id: "ACTA0108",
        title: "A job on a schedule faster than once a minute should state what it keeps",
        messageFormat: "{0}",
        category: "Acta",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpBase + "acta0108"
    );

    /// <summary>ACTA0108: an interval under a minute (warning; the job stays in the manifest).</summary>
    public static DiagnosticRecord FrequentInterval(DiscoveredJob job, DiscoveredSchedule schedule, long firingsPerYear) =>
        new(
            "ACTA0108",
            $"`[Schedule(\"{schedule.ScheduleName}\")]` on `[Job(\"{job.JobName}\")]` fires every `{schedule.Expression}`, {firingsPerYear.ToString("N0", CultureInfo.InvariantCulture)} times a year, {WhatFiringsKeep(job)}",
            job.Location
        );

    /// <summary>ACTA0108: a cron that can fire more than once a minute (warning; the job stays in the manifest).</summary>
    public static DiagnosticRecord FrequentCron(DiscoveredJob job, DiscoveredSchedule schedule) =>
        new(
            "ACTA0108",
            $"`[Schedule(\"{schedule.ScheduleName}\")]` on `[Job(\"{job.JobName}\")]` has the cron `{schedule.Expression}`, which can fire more than once a minute, {WhatFiringsKeep(job)}",
            job.Location
        );

    private static string WhatFiringsKeep(DiscoveredJob job) =>
        job.ScheduledDeliveryName == "Permanent"
            ? "and every firing writes events, kept for `JobsOptions.Retention`. State `EventRecording` on the job to say which events to keep, for example `EventRecording = EventRecordingCode.Failures`."
            : "and every firing makes a new job, kept for its `JobRetention`, and writes events, kept for `JobsOptions.Retention`. State both on the job, for example `EventRecording = EventRecordingCode.Failures, JobRetention = \"1d\"`.";
}
