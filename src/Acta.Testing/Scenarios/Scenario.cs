namespace Acta.Testing.Scenarios;

/// <summary>
/// Entry points for Scenario Studio. Create a typed enqueue builder, enqueue once, then drive the
/// returned <see cref="ScenarioSession{TInput}"/> deterministically.
/// </summary>
public static class Scenario
{
    public static ScenarioTypedBuilder<TInput> For<TInput>(IActaTestHost host)
        where TInput : notnull => new(host);

    public static ScenarioTypedBuilder<TInput, TResult> For<TInput, TResult>(IActaTestHost host)
        where TInput : notnull
        where TResult : notnull => new(host);

    public static ScenarioContractBuilder<TInput> For<TInput>(JobContract<TInput> contract, IActaTestHost host)
        where TInput : notnull => new(host, contract);

    public static ScenarioContractBuilder<TInput, TResult> For<TInput, TResult>(JobContract<TInput, TResult> contract, IActaTestHost host)
        where TInput : notnull
        where TResult : notnull => new(host, contract);

    public static ScenarioNoInputBuilder For(JobContract<NoInput> contract, IActaTestHost host) => new(host, contract);

    public static ScenarioNoInputBuilder<TResult> For<TResult>(JobContract<NoInput, TResult> contract, IActaTestHost host)
        where TResult : notnull => new(host, contract);
}

/// <summary>Typed enqueue builder resolved by input type.</summary>
public sealed class ScenarioTypedBuilder<TInput>
    where TInput : notnull
{
    private readonly IActaTestHost _host;

    internal ScenarioTypedBuilder(IActaTestHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    public async ValueTask<ScenarioSession<TInput>> EnqueueAsync(
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
    {
        var outcome = await _host.Jobs.EnqueueAsync(input, options, ct);
        return await ScenarioSessionFactory.CreateAsync<TInput>(_host, outcome, ct);
    }
}

/// <summary>Typed enqueue builder resolved by input type, with a typed result session.</summary>
public sealed class ScenarioTypedBuilder<TInput, TResult>
    where TInput : notnull
    where TResult : notnull
{
    private readonly IActaTestHost _host;

    internal ScenarioTypedBuilder(IActaTestHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    public async ValueTask<ScenarioSession<TInput, TResult>> EnqueueAsync(
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
    {
        var outcome = await _host.Jobs.EnqueueAsync(input, options, ct);
        return await ScenarioSessionFactory.CreateAsync<TInput, TResult>(_host, outcome, ct);
    }
}

/// <summary>Contract-based enqueue builder for ambiguous or explicitly named jobs.</summary>
public sealed class ScenarioContractBuilder<TInput>
    where TInput : notnull
{
    private readonly IActaTestHost _host;
    private readonly JobContract<TInput> _contract;

    internal ScenarioContractBuilder(IActaTestHost host, JobContract<TInput> contract)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _contract = contract;
    }

    public async ValueTask<ScenarioSession<TInput>> EnqueueAsync(
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
    {
        var outcome = await _host.Jobs.EnqueueAsync(_contract, input, options, ct);
        return await ScenarioSessionFactory.CreateAsync<TInput>(_host, outcome, ct);
    }
}

/// <summary>Contract-based enqueue builder for jobs with a typed result.</summary>
public sealed class ScenarioContractBuilder<TInput, TResult>
    where TInput : notnull
    where TResult : notnull
{
    private readonly IActaTestHost _host;
    private readonly JobContract<TInput, TResult> _contract;

    internal ScenarioContractBuilder(IActaTestHost host, JobContract<TInput, TResult> contract)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _contract = contract;
    }

    public async ValueTask<ScenarioSession<TInput, TResult>> EnqueueAsync(
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
    {
        var outcome = await _host.Jobs.EnqueueAsync(_contract, input, options, ct);
        return await ScenarioSessionFactory.CreateAsync<TInput, TResult>(_host, outcome, ct);
    }
}

/// <summary>Contract-based enqueue builder for no-input jobs.</summary>
public sealed class ScenarioNoInputBuilder
{
    private readonly IActaTestHost _host;
    private readonly JobContract<NoInput> _contract;

    internal ScenarioNoInputBuilder(IActaTestHost host, JobContract<NoInput> contract)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _contract = contract;
    }

    public async ValueTask<ScenarioSession<NoInput>> EnqueueAsync(JobEnqueueOptions? options = null, CancellationToken ct = default)
    {
        var outcome = await _host.Jobs.EnqueueAsync(_contract, options, ct);
        return await ScenarioSessionFactory.CreateAsync<NoInput>(_host, outcome, ct);
    }
}

/// <summary>Contract-based enqueue builder for no-input jobs with a typed result.</summary>
public sealed class ScenarioNoInputBuilder<TResult>
    where TResult : notnull
{
    private readonly IActaTestHost _host;
    private readonly JobContract<NoInput, TResult> _contract;

    internal ScenarioNoInputBuilder(IActaTestHost host, JobContract<NoInput, TResult> contract)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _contract = contract;
    }

    public async ValueTask<ScenarioSession<NoInput, TResult>> EnqueueAsync(
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
    {
        var outcome = await _host.Jobs.EnqueueAsync(_contract, default, options, ct);
        return await ScenarioSessionFactory.CreateAsync<NoInput, TResult>(_host, outcome, ct);
    }
}

internal static class ScenarioSessionFactory
{
    public static async ValueTask<ScenarioSession<TInput>> CreateAsync<TInput>(
        IActaTestHost host,
        JobEnqueueOutcome outcome,
        CancellationToken ct
    )
        where TInput : notnull
    {
        var snapshot =
            await host.Jobs.GetAsync(outcome, ct)
            ?? throw new InvalidOperationException($"Scenario enqueue produced job {outcome.JobId}, but the row could not be read.");
        return new ScenarioSession<TInput>(host, outcome, snapshot.JobNamespace, snapshot.JobName);
    }

    public static async ValueTask<ScenarioSession<TInput, TResult>> CreateAsync<TInput, TResult>(
        IActaTestHost host,
        JobEnqueueOutcome outcome,
        CancellationToken ct
    )
        where TInput : notnull
        where TResult : notnull
    {
        var snapshot =
            await host.Jobs.GetAsync(outcome, ct)
            ?? throw new InvalidOperationException($"Scenario enqueue produced job {outcome.JobId}, but the row could not be read.");
        return new ScenarioSession<TInput, TResult>(host, outcome, snapshot.JobNamespace, snapshot.JobName);
    }
}
