using Xunit;

namespace Acta.Tests.Cli;

public class CliCommandParserTests
{
    [Fact]
    public void Empty_args_parse_as_help()
    {
        Assert.True(CliCommandParser.TryParse([], out var cmd, out _));
        Assert.Equal(CliVerb.Help, cmd.Verb);
    }

    [Fact]
    public void Help_verb_parses_as_help()
    {
        Assert.True(CliCommandParser.TryParse(["help"], out var cmd, out _));
        Assert.Equal(CliVerb.Help, cmd.Verb);
    }

    [Theory]
    [InlineData("info", CliVerb.Info)]
    [InlineData("status", CliVerb.Status)]
    [InlineData("result", CliVerb.Result)]
    [InlineData("cancel", CliVerb.Cancel)]
    [InlineData("pause", CliVerb.Pause)]
    [InlineData("resume", CliVerb.Resume)]
    [InlineData("restart", CliVerb.Restart)]
    [InlineData("debug", CliVerb.Debug)]
    [InlineData("explain", CliVerb.Explain)]
    // internal because CliVerb is internal (CS0051); xUnit v3 discovers internal theories.
    internal void Target_verbs_capture_target(string verb, CliVerb expected)
    {
        Assert.True(CliCommandParser.TryParse([verb, "123"], out var cmd, out _));
        Assert.Equal(expected, cmd.Verb);
        Assert.Equal("123", cmd.Target);
    }

    [Fact]
    public void Events_captures_target_take_and_after()
    {
        Assert.True(CliCommandParser.TryParse(["events", "job_1", "--take", "100", "--after", "cur"], out var cmd, out _));
        Assert.Equal(CliVerb.Events, cmd.Verb);
        Assert.Equal("job_1", cmd.Target);
        Assert.Equal(100, cmd.Take);
        Assert.Equal("cur", cmd.Cursor);
    }

    [Theory]
    [InlineData("events", "1", "--take", "0")] // non-positive take
    [InlineData("events", "1", "--take", "abc")] // non-numeric take
    [InlineData("info", "1", "--take", "10")] // take on a non-events verb
    [InlineData("pause", "1", "--after", "cur")] // after on a non-events verb
    public void Malformed_events_paging_is_an_error(params string[] args)
    {
        Assert.False(CliCommandParser.TryParse(args, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Signal_captures_target_and_name()
    {
        Assert.True(CliCommandParser.TryParse(["signal", "123", "approval"], out var cmd, out _));
        Assert.Equal(CliVerb.Signal, cmd.Verb);
        Assert.Equal("123", cmd.Target);
        Assert.Equal("approval", cmd.SignalName);
        Assert.Null(cmd.SignalValue);
    }

    [Fact]
    public void Signal_captures_value_flag()
    {
        Assert.True(CliCommandParser.TryParse(["signal", "123", "approval", "--value", "{\"ok\":true}"], out var cmd, out _));
        Assert.Equal(CliVerb.Signal, cmd.Verb);
        Assert.Equal("approval", cmd.SignalName);
        Assert.Equal("{\"ok\":true}", cmd.SignalValue);
    }

    [Fact]
    public void Value_flag_is_rejected_for_non_signal_verb()
    {
        Assert.False(CliCommandParser.TryParse(["pause", "1", "--value", "{}"], out _, out var error));
        Assert.Contains("--value", error);
    }

    [Fact]
    public void Flags_parse_reason_ns_json()
    {
        Assert.True(
            CliCommandParser.TryParse(["pause", "order-9", "--reason", "deploy window", "--ns", "shop", "--json"], out var cmd, out _)
        );
        Assert.Equal("order-9", cmd.Target);
        Assert.Equal("deploy window", cmd.Reason);
        Assert.Equal("shop", cmd.Namespace);
        Assert.True(cmd.Json);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("enqueue")]
    public void Unknown_verb_is_an_error(string verb)
    {
        Assert.False(CliCommandParser.TryParse([verb, "123"], out _, out var error));
        Assert.Contains(verb, error);
    }

    [Fact]
    public void Missing_target_parses_with_null_target()
    {
        Assert.True(CliCommandParser.TryParse(["pause"], out var cmd, out _));
        Assert.Equal(CliVerb.Pause, cmd.Verb);
        Assert.Null(cmd.Target);
    }

    // A signal is a control, so its job never comes from the clipboard: one positional is a job without a name.
    [Fact]
    public void Signal_with_one_positional_is_an_error()
    {
        Assert.False(CliCommandParser.TryParse(["signal", "approval"], out _, out var error));
        Assert.Contains("requires a job and a signal name", error);
    }

    // The parser keeps the target as typed; the runner decides what a ref, an id:<n>, or a key addresses.
    [Theory]
    [InlineData("4711")]
    [InlineData("id:42")]
    [InlineData("job_019826f000007000800000000000002a")]
    public void A_target_parses_as_typed(string target)
    {
        Assert.True(CliCommandParser.TryParse(["cancel", target, "--ns", "billing"], out var cmd, out _));
        Assert.Equal(target, cmd.Target);
        Assert.Equal("billing", cmd.Namespace);
    }

    [Theory]
    [InlineData("signal")] // missing signal name
    [InlineData("pause", "1", "extra")] // stray positional
    [InlineData("signal", "1", "go", "extra")] // stray positional
    [InlineData("pause", "1", "--reason")] // flag without value
    [InlineData("pause", "1", "--ns")] // flag without value
    [InlineData("pause", "1", "--bogus", "x")] // unknown flag
    [InlineData("info", "1", "--reason", "x")] // reason on a read verb
    [InlineData("debug", "1", "--reason", "x")] // reason on debug (the runner stamps its own)
    [InlineData("migrate", "job_1")] // migrate applies the configured schema, never a job's
    [InlineData("migrate", "--ns", "shop")]
    [InlineData("migrate", "--reason", "now")]
    public void Malformed_commands_are_errors(params string[] args)
    {
        Assert.False(CliCommandParser.TryParse(args, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Migrate_parses_with_no_target_and_takes_json()
    {
        Assert.True(CliCommandParser.TryParse(["migrate"], out var plain, out _));
        Assert.Equal(CliVerb.Migrate, plain.Verb);
        Assert.Null(plain.Target);

        Assert.True(CliCommandParser.TryParse(["migrate", "--json"], out var json, out _));
        Assert.True(json.Json);
    }

    [Fact]
    public void Deploy_parses_with_no_target_and_takes_force_and_json()
    {
        Assert.True(CliCommandParser.TryParse(["deploy"], out var plain, out _));
        Assert.Equal(CliVerb.Deploy, plain.Verb);
        Assert.Null(plain.Target);
        Assert.False(plain.Force);

        Assert.True(CliCommandParser.TryParse(["deploy", "--force", "--json"], out var forced, out _));
        Assert.True(forced.Force);
        Assert.True(forced.Json);
    }

    [Fact]
    public void Migrate_takes_force_for_the_deploy_it_runs()
    {
        Assert.True(CliCommandParser.TryParse(["migrate", "--force"], out var cmd, out _));
        Assert.Equal(CliVerb.Migrate, cmd.Verb);
        Assert.True(cmd.Force);
    }

    [Theory]
    [InlineData("deploy", "job_0123")]
    [InlineData("migrate", "job_0123")]
    public void Deploy_and_migrate_reject_a_target(string verb, string target)
    {
        Assert.False(CliCommandParser.TryParse([verb, target], out _, out var error));
        Assert.Contains(verb, error);
    }

    [Fact]
    public void Force_flag_is_rejected_for_any_other_verb()
    {
        Assert.False(CliCommandParser.TryParse(["status", "1", "--force"], out _, out var error));
        Assert.Contains("--force", error);
    }

    [Theory]
    [InlineData(new[] { "app.dll", "jobs", "migrate" }, new[] { "migrate" })]
    [InlineData(new[] { "app.dll", "--environment", "Staging", "jobs", "migrate" }, new[] { "migrate" })]
    [InlineData(new[] { "app.dll", "--environment=Staging", "jobs", "migrate", "--json" }, new[] { "migrate", "--json" })]
    [InlineData(new[] { "app.dll", "Environment=Staging", "jobs", "migrate" }, new[] { "migrate" })]
    [InlineData(new[] { "app.dll", "/environment", "Staging", "-v", "jobs", "info", "1" }, new[] { "info", "1" })]
    public void Jobs_after_host_switches_starts_the_cli(string[] commandLine, string[] expected) =>
        Assert.Equal(expected, CliCommandParser.DetectInvocation(commandLine));

    [Theory]
    [InlineData(new object[] { new[] { "app.dll" } })]
    [InlineData(new object[] { new[] { "app.dll", "--config", "jobs" } })] // the host reads jobs as the switch's value
    [InlineData(new object[] { new[] { "app.dll", "--verbose", "jobs", "migrate" } })] // likewise: --verbose takes jobs
    [InlineData(new object[] { new[] { "app.dll", "serve", "jobs" } })] // a positional first is the app's own command
    public void Other_command_lines_boot_the_host(string[] commandLine) => Assert.Null(CliCommandParser.DetectInvocation(commandLine));

    [Fact]
    public void Debug_break_flag_sets_break()
    {
        Assert.True(CliCommandParser.TryParse(["debug", "123", "--break"], out var cmd, out _));
        Assert.Equal(CliVerb.Debug, cmd.Verb);
        Assert.Equal("123", cmd.Target);
        Assert.True(cmd.Break);
    }

    [Fact]
    public void Debug_without_break_flag_leaves_break_false()
    {
        Assert.True(CliCommandParser.TryParse(["debug", "123"], out var cmd, out _));
        Assert.False(cmd.Break);
    }

    [Fact]
    public void Break_flag_is_rejected_for_non_debug_verb()
    {
        Assert.False(CliCommandParser.TryParse(["status", "1", "--break"], out _, out var error));
        Assert.Contains("--break", error);
    }

    [Fact]
    public void Detect_returns_cli_args_when_first_user_arg_is_jobs()
    {
        var cli = CliCommandParser.DetectInvocation(["C:\\app\\myapp.exe", "jobs", "pause", "123"]);
        Assert.NotNull(cli);
        Assert.Equal(["pause", "123"], cli);
    }

    [Fact]
    public void Detect_returns_null_otherwise()
    {
        Assert.Null(CliCommandParser.DetectInvocation(["C:\\app\\myapp.exe"]));
        Assert.Null(CliCommandParser.DetectInvocation(["C:\\app\\myapp.exe", "serve"]));
        Assert.Null(CliCommandParser.DetectInvocation(["C:\\app\\myapp.exe", "--jobs"]));
    }
}
