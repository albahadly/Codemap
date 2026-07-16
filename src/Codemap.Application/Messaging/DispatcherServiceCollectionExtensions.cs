using System.Reflection;
using Codemap.Application.Behaviors;
using Microsoft.Extensions.DependencyInjection;

namespace Codemap.Application.Messaging;

public static class DispatcherServiceCollectionExtensions
{
    /// <summary>
    /// Registers the dispatcher, the two default pipeline behaviors (Logging outermost, then ScanProgress),
    /// and every <see cref="IRequestHandler{TRequest,TResponse}"/> / <see cref="INotificationHandler{TNotification}"/> /
    /// closed <see cref="IPipelineBehavior{TRequest,TResponse}"/> found in the given assemblies.
    /// Open-generic behavior types found while scanning are registered as open generics.
    /// </summary>
    public static IServiceCollection AddCustomDispatcher(this IServiceCollection services, params Assembly[] assemblies)
    {
        if (assemblies.Length == 0)
            assemblies = [typeof(DispatcherServiceCollectionExtensions).Assembly];

        services.AddScoped<IDispatcher, Dispatcher>();

        // Default behaviors — registration order defines wrapping order (first registered = outermost).
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ScanProgressBehavior<,>));

        foreach (var assembly in assemblies.Distinct())
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || type.IsInterface) continue;

                var messagingInterfaces = type.GetInterfaces()
                    .Where(i => i.IsGenericType)
                    .Where(i =>
                    {
                        var def = i.GetGenericTypeDefinition();
                        return def == typeof(IRequestHandler<,>)
                            || def == typeof(INotificationHandler<>)
                            || def == typeof(IPipelineBehavior<,>);
                    });

                foreach (var serviceInterface in messagingInterfaces)
                {
                    if (type.IsGenericTypeDefinition)
                    {
                        // Open-generic behavior (or handler) discovered by scanning; the two defaults are
                        // registered explicitly above so their order is deterministic — skip duplicates.
                        if (type == typeof(LoggingBehavior<,>) || type == typeof(ScanProgressBehavior<,>)) continue;
                        services.AddTransient(serviceInterface.GetGenericTypeDefinition(), type);
                    }
                    else
                    {
                        services.AddTransient(serviceInterface, type);
                    }
                }
            }
        }

        return services;
    }
}
