using System.Collections.Immutable;
using Acta.Runtime.Modules.Alerting.Api;
using Acta.Runtime.Modules.Execution.Api;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Acta.Runtime.Modules.Alerting;

/// <summary>
/// Alerting's implementation of Execution's startup routing seam: every alerting definition must
/// resolve to a channel configured for its worker namespace - the declared AlertChannelName, else
/// the implicit "default". Disabled or deprecated channels count as configured here; delivery
/// policy decides whether a concrete alert is sent.
/// </summary>
internal sealed class AlertRoutingCheck(
    IAlertChannelRegistry channels,
    IOptions<JobsOptions> options,
    ILogger<AlertRoutingCheck>? log = null
) : IAlertRoutingCheck
{
    // The one implicit channel: every alert (user or sys-critical) with no declared
    // AlertChannelName routes here, so failures deliver out of the box to the log transport without
    // any operator config. Operators override it, or add more channels, via AddAlertChannel.
    private const string DefaultAlertChannelName = "default";

    private readonly ILogger _log = log ?? (ILogger)NullLogger.Instance;

    public void ValidateRouting(string namespaceName, ImmutableArray<JobDescriptor> effectiveDescriptors)
    {
        var mode = options.Value.AlertChannelValidationMode;
        if (mode == AlertChannelValidationMode.Off)
        {
            return;
        }

        foreach (var descriptor in effectiveDescriptors)
        {
            if (descriptor.AlertProfile == AlertProfileCode.None)
            {
                continue;
            }

            var channel = descriptor.AlertChannelName ?? DefaultAlertChannelName;

            if (channels.IsConfigured(namespaceName, channel))
            {
                continue;
            }

            var message =
                $"Job '{descriptor.JobName}' routes alerts to channel '{channel}', but worker namespace "
                + $"'{namespaceName}' does not configure that channel. "
                + $"Add w.AddAlertChannel(\"{channel}\", ...).";

            if (mode == AlertChannelValidationMode.Fail)
            {
                throw new InvalidOperationException(message);
            }

            _log.LogWarning(
                "Acta alerting: job {JobName} routes to unconfigured channel '{Detail}' in namespace {Namespace}; add it to worker startup configuration with w.AddAlertChannel(...).",
                descriptor.JobName,
                channel,
                namespaceName
            );
        }
    }
}
