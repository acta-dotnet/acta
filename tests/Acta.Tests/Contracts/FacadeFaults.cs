using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Acta.Tests.Contracts;

/// <summary>
/// The facades the generating host serves, each a pass-through to the staged fakes until <see cref="Fault"/> is set: then
/// a call fails as a server bug does, which the host answers 500, or as a database that never answers does, the 503.
/// </summary>
internal sealed class FacadeFaults
{
    public FacadeFault Fault { get; set; }

    public T Wrap<T>(T target)
        where T : class => (T)Wrap(typeof(T), target);

    private object Wrap(Type facade, object target)
    {
        var proxy = DispatchProxy.Create(facade, typeof(FaultingFacade));
        ((FaultingFacade)proxy).Bind(target, this);
        return proxy;
    }

    /// <summary>
    /// One facade behind the switch. A facade property answers its facade behind the same switch; an unreachable database
    /// fails only the asynchronous calls, since no database sits behind a synchronous one.
    /// </summary>
    internal class FaultingFacade : DispatchProxy
    {
        private object target = null!;
        private FacadeFaults faults = null!;

        public void Bind(object facade, FacadeFaults owner)
        {
            target = facade;
            faults = owner;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod!;
            var returns = method.ReturnType;
            if (returns.IsInterface && returns.Namespace == nameof(Acta))
            {
                return faults.Wrap(returns, Forward(method, args)!);
            }

            var asynchronous =
                typeof(Task).IsAssignableFrom(returns)
                || returns == typeof(ValueTask)
                || returns.IsGenericType && returns.GetGenericTypeDefinition() == typeof(ValueTask<>);
            return faults.Fault switch
            {
                FacadeFault.Server => throw new ServerFaultException(),
                FacadeFault.Database when asynchronous => throw new TimeoutException("The database did not answer."),
                _ => Forward(method, args),
            };
        }

        private object? Forward(MethodInfo method, object?[]? args)
        {
            try
            {
                return method.Invoke(target, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
            {
                ExceptionDispatchInfo.Throw(inner);
                throw;
            }
        }
    }

    /// <summary>A failure no route expects, so the error backstop answers it as the server's own.</summary>
    private sealed class ServerFaultException() : Exception("The facade failed.");
}

/// <summary>How the facades behind <see cref="FacadeFaults"/> fail: not at all, as a server bug, or as an unreachable database.</summary>
internal enum FacadeFault
{
    None,
    Server,
    Database,
}
