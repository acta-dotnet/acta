namespace Acta.Cli;

/// <summary>
/// Hand-rolled parser for the jobs CLI: verb, positional target (and signal name), then flags.
/// Grammar: jobs verb [target] [name] [--reason msg] [--value json] [--ns ns] [--json]. A missing
/// target parses as null; the runner fills it from the clipboard.
/// </summary>
internal static class CliCommandParser
{
    /// <summary>The reserved first process argument that switches the host into CLI mode.</summary>
    public const string ReservedWord = "jobs";

    /// <summary>
    /// Inspects the full command line (index 0 is the executable path). Returns the args after the
    /// reserved word when the first user argument is exactly "jobs", null otherwise.
    /// </summary>
    public static string[]? DetectInvocation(string[] commandLineArgs) =>
        commandLineArgs.Length >= 2 && string.Equals(commandLineArgs[1], ReservedWord, StringComparison.Ordinal)
            ? commandLineArgs[2..]
            : null;

    /// <summary>
    /// Parses the args after the reserved word into a CliCommand. Returns false with a usage
    /// message in <paramref name="error"/> on any malformed input; command is then Help.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string> args, out CliCommand command, out string? error)
    {
        command = new CliCommand(CliVerb.Help, null, null, null, null, null, Json: false);
        error = null;

        // help ignores any trailing tokens; everything after it is irrelevant to usage output.
        if (args.Count == 0 || string.Equals(args[0], "help", StringComparison.Ordinal))
        {
            return true;
        }

        var verb = args[0] switch
        {
            "info" => CliVerb.Info,
            "status" => CliVerb.Status,
            "result" => CliVerb.Result,
            "cancel" => CliVerb.Cancel,
            "pause" => CliVerb.Pause,
            "resume" => CliVerb.Resume,
            "restart" => CliVerb.Restart,
            "signal" => CliVerb.Signal,
            "debug" => CliVerb.Debug,
            "events" => CliVerb.Events,
            "explain" => CliVerb.Explain,
            _ => (CliVerb?)null,
        };
        if (verb is null)
        {
            error = $"Unknown verb '{args[0]}'. Run 'jobs help' for usage.";
            return false;
        }

        string? target = null;
        string? signalName = null;
        string? signalValue = null;
        string? reason = null;
        string? ns = null;
        int? take = null;
        string? cursor = null;
        var json = false;
        var brk = false;

        for (var i = 1; i < args.Count; i++)
        {
            var token = args[i];
            switch (token)
            {
                case "--json":
                    json = true;
                    break;
                case "--break":
                    brk = true;
                    break;
                case "--reason" or "--ns" or "--value" or "--take" or "--after":
                    if (i + 1 >= args.Count)
                    {
                        error = $"Flag '{token}' requires a value.";
                        return false;
                    }
                    var flagValue = args[++i];
                    if (string.Equals(token, "--reason", StringComparison.Ordinal))
                    {
                        reason = flagValue;
                    }
                    else if (string.Equals(token, "--ns", StringComparison.Ordinal))
                    {
                        ns = flagValue;
                    }
                    else if (string.Equals(token, "--take", StringComparison.Ordinal))
                    {
                        if (!int.TryParse(flagValue, out var parsed) || parsed <= 0)
                        {
                            error = "Flag '--take' requires a positive integer.";
                            return false;
                        }
                        take = parsed;
                    }
                    else if (string.Equals(token, "--after", StringComparison.Ordinal))
                    {
                        cursor = flagValue;
                    }
                    else
                    {
                        signalValue = flagValue;
                    }
                    break;
                default:
                    if (token.StartsWith("--", StringComparison.Ordinal))
                    {
                        error = $"Unknown flag '{token}'.";
                        return false;
                    }
                    if (target is null)
                    {
                        target = token;
                    }
                    else if (verb == CliVerb.Signal && signalName is null)
                    {
                        signalName = token;
                    }
                    else
                    {
                        error = $"Unexpected argument '{token}'.";
                        return false;
                    }
                    break;
            }
        }

        if (verb == CliVerb.Signal && signalName is null)
        {
            // A lone positional on signal is the name; the target then comes from the clipboard.
            (target, signalName) = (null, target);
            if (signalName is null)
            {
                error = "Verb 'signal' requires a signal name.";
                return false;
            }
        }
        if (reason is not null && verb is not (CliVerb.Cancel or CliVerb.Pause or CliVerb.Resume or CliVerb.Restart))
        {
            error = $"Flag '--reason' is not valid for verb '{args[0]}'.";
            return false;
        }
        if (signalValue is not null && verb != CliVerb.Signal)
        {
            error = $"Flag '--value' is only valid for verb 'signal'.";
            return false;
        }
        if ((take is not null || cursor is not null) && verb != CliVerb.Events)
        {
            error = $"Flags '--take'/'--after' are only valid for verb 'events'.";
            return false;
        }
        if (brk && verb != CliVerb.Debug)
        {
            error = $"Flag '--break' is only valid for verb 'debug'.";
            return false;
        }

        command = new CliCommand(verb.Value, target, signalName, signalValue, reason, ns, json, take, cursor, brk);
        return true;
    }
}
