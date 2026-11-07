using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Tests.AspNetCore.Fakes;

/// <summary>
/// Facades over a database that never answers: every asynchronous call throws <see cref="TimeoutException"/>, as a
/// provider's command timeout does. A facade property answers another such facade, and a synchronous call its default,
/// since no database sits behind one. <see cref="Register"/> puts them in front of a test host's fakes.
/// </summary>
internal class UnreachableDatabase : DispatchProxy
{
    public static void Register(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton((IJobs)Create(typeof(IJobs)));
        builder.Services.AddSingleton((IActaOperations)Create(typeof(IActaOperations)));
    }

    private static object Create(Type facade) => Create(facade, typeof(UnreachableDatabase));

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var returns = targetMethod!.ReturnType;
        if (returns.IsInterface && returns.Namespace == nameof(Acta))
        {
            return Create(returns);
        }

        if (
            typeof(Task).IsAssignableFrom(returns)
            || returns == typeof(ValueTask)
            || returns.IsGenericType && returns.GetGenericTypeDefinition() == typeof(ValueTask<>)
        )
        {
            throw new TimeoutException("The database did not answer.");
        }

        return returns.IsValueType && returns != typeof(void) ? Activator.CreateInstance(returns) : null;
    }
}
