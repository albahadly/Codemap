using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Codemap.Application.Messaging;

/// <summary>
/// CQRS-lite dispatcher. <see cref="Send{TResponse}"/> resolves the handler for the request's runtime type
/// (dynamic dispatch via cached reflection plans, so a call site holding only <c>IRequest&lt;T&gt;</c> still
/// reaches the concrete handler), wraps it in every registered <see cref="IPipelineBehavior{TRequest,TResponse}"/>
/// in reverse registration order (first registered behavior runs outermost), and executes the chain.
/// <see cref="Publish{TNotification}"/> fans out to every registered notification handler sequentially.
/// </summary>
public sealed class Dispatcher(IServiceProvider services) : IDispatcher
{
    private static readonly ConcurrentDictionary<Type, DispatchPlan> SendPlans = new();
    private static readonly ConcurrentDictionary<Type, PublishPlan> PublishPlans = new();

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = SendPlans.GetOrAdd(request.GetType(), static (requestType, responseType) =>
        {
            var handlerInterface = typeof(IRequestHandler<,>).MakeGenericType(requestType, responseType);
            var behaviorInterface = typeof(IPipelineBehavior<,>).MakeGenericType(requestType, responseType);
            return new DispatchPlan(
                handlerInterface,
                handlerInterface.GetMethod("Handle")!,
                behaviorInterface,
                behaviorInterface.GetMethod("Handle")!);
        }, typeof(TResponse));

        var handler = services.GetService(plan.HandlerInterface)
            ?? throw new InvalidOperationException(
                $"No handler registered for {request.GetType().Name} → {typeof(TResponse).Name}. " +
                $"Did you forget to include its assembly in AddCustomDispatcher()?");

        Func<Task<TResponse>> next = () => Unwrap<TResponse>(() => plan.HandlerHandle.Invoke(handler, [request, ct]));

        var behaviors = services.GetServices(plan.BehaviorInterface).ToList();
        for (var i = behaviors.Count - 1; i >= 0; i--)
        {
            var behavior = behaviors[i]!;
            var inner = next;
            next = () => Unwrap<TResponse>(() => plan.BehaviorHandle.Invoke(behavior, [request, inner, ct]));
        }

        return next();
    }

    public async Task Publish<TNotification>(TNotification notification, CancellationToken ct = default)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);
        // Resolve by runtime type (not the generic parameter) so publishing through a base/interface
        // reference still reaches the handlers registered for the concrete notification type.
        var plan = PublishPlans.GetOrAdd(notification.GetType(), static notificationType =>
        {
            var handlerInterface = typeof(INotificationHandler<>).MakeGenericType(notificationType);
            return new PublishPlan(handlerInterface, handlerInterface.GetMethod("Handle")!);
        });

        foreach (var handler in services.GetServices(plan.HandlerInterface))
        {
            ct.ThrowIfCancellationRequested();
            await Unwrap(() => plan.HandlerHandle.Invoke(handler, [notification, ct])).ConfigureAwait(false);
        }
    }

    /// <summary>Invokes via reflection, unwrapping TargetInvocationException so callers see the real failure.</summary>
    private static Task<TResponse> Unwrap<TResponse>(Func<object?> invoke)
    {
        try { return (Task<TResponse>)invoke()!; }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            return Task.FromException<TResponse>(ex.InnerException);
        }
    }

    private static Task Unwrap(Func<object?> invoke)
    {
        try { return (Task)invoke()!; }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            return Task.FromException(ex.InnerException);
        }
    }

    private sealed record DispatchPlan(Type HandlerInterface, MethodInfo HandlerHandle, Type BehaviorInterface, MethodInfo BehaviorHandle);
    private sealed record PublishPlan(Type HandlerInterface, MethodInfo HandlerHandle);
}
