using Codemap.Application.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Codemap.Tests.Messaging;

public class DispatcherTests
{
    // ── fixtures ─────────────────────────────────────────────────────────────────

    public sealed record Ping(string Message) : IRequest<string>;

    public sealed class PingHandler : IRequestHandler<Ping, string>
    {
        public Task<string> Handle(Ping request, CancellationToken ct = default) =>
            Task.FromResult($"pong:{request.Message}");
    }

    public sealed class ExecutionLog
    {
        public List<string> Entries { get; } = [];
    }

    public sealed class OuterBehavior(ExecutionLog log) : IPipelineBehavior<Ping, string>
    {
        public async Task<string> Handle(Ping request, Func<Task<string>> next, CancellationToken ct = default)
        {
            log.Entries.Add("outer:before");
            var result = await next();
            log.Entries.Add("outer:after");
            return result;
        }
    }

    public sealed class InnerBehavior(ExecutionLog log) : IPipelineBehavior<Ping, string>
    {
        public async Task<string> Handle(Ping request, Func<Task<string>> next, CancellationToken ct = default)
        {
            log.Entries.Add("inner:before");
            var result = await next();
            log.Entries.Add("inner:after");
            return result;
        }
    }

    public sealed record Waved : INotification;

    public sealed class WaveCounter
    {
        public int Count;
    }

    public sealed class FirstWaveHandler(WaveCounter counter) : INotificationHandler<Waved>
    {
        public Task Handle(Waved notification, CancellationToken ct = default)
        {
            Interlocked.Increment(ref counter.Count);
            return Task.CompletedTask;
        }
    }

    public sealed class SecondWaveHandler(WaveCounter counter) : INotificationHandler<Waved>
    {
        public Task Handle(Waved notification, CancellationToken ct = default)
        {
            Interlocked.Increment(ref counter.Count);
            return Task.CompletedTask;
        }
    }

    public sealed record Boom : IRequest<int>;

    public sealed class BoomHandler : IRequestHandler<Boom, int>
    {
        public Task<int> Handle(Boom request, CancellationToken ct = default) =>
            throw new InvalidOperationException("kaboom");
    }

    // ── tests ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Send_resolves_the_handler_for_the_runtime_request_type()
    {
        var services = new ServiceCollection()
            .AddScoped<IDispatcher, Dispatcher>()
            .AddTransient<IRequestHandler<Ping, string>, PingHandler>()
            .BuildServiceProvider();

        // dispatch through the interface — the handler must still be found via the runtime type
        IRequest<string> request = new Ping("hello");
        var response = await services.GetRequiredService<IDispatcher>().Send(request);

        Assert.Equal("pong:hello", response);
    }

    [Fact]
    public async Task Behaviors_wrap_in_registration_order_first_registered_outermost()
    {
        var services = new ServiceCollection()
            .AddScoped<IDispatcher, Dispatcher>()
            .AddSingleton<ExecutionLog>()
            .AddTransient<IRequestHandler<Ping, string>, PingHandler>()
            .AddTransient<IPipelineBehavior<Ping, string>, OuterBehavior>()
            .AddTransient<IPipelineBehavior<Ping, string>, InnerBehavior>()
            .BuildServiceProvider();

        await services.GetRequiredService<IDispatcher>().Send(new Ping("x"));

        Assert.Equal(
            ["outer:before", "inner:before", "inner:after", "outer:after"],
            services.GetRequiredService<ExecutionLog>().Entries);
    }

    [Fact]
    public async Task Publish_fans_out_to_every_notification_handler()
    {
        var services = new ServiceCollection()
            .AddScoped<IDispatcher, Dispatcher>()
            .AddSingleton<WaveCounter>()
            .AddTransient<INotificationHandler<Waved>, FirstWaveHandler>()
            .AddTransient<INotificationHandler<Waved>, SecondWaveHandler>()
            .BuildServiceProvider();

        await services.GetRequiredService<IDispatcher>().Publish(new Waved());

        Assert.Equal(2, services.GetRequiredService<WaveCounter>().Count);
    }

    [Fact]
    public async Task Send_without_a_registered_handler_throws_a_descriptive_error()
    {
        var services = new ServiceCollection()
            .AddScoped<IDispatcher, Dispatcher>()
            .BuildServiceProvider();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => services.GetRequiredService<IDispatcher>().Send(new Ping("x")));
        Assert.Contains(nameof(Ping), ex.Message);
    }

    [Fact]
    public async Task Handler_exceptions_surface_unwrapped_not_as_TargetInvocationException()
    {
        var services = new ServiceCollection()
            .AddScoped<IDispatcher, Dispatcher>()
            .AddTransient<IRequestHandler<Boom, int>, BoomHandler>()
            .BuildServiceProvider();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => services.GetRequiredService<IDispatcher>().Send(new Boom()));
        Assert.Equal("kaboom", ex.Message);
    }

    [Fact]
    public async Task AddCustomDispatcher_scans_handlers_and_runs_them_through_default_behaviors()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ExecutionLog>();
        services.AddSingleton<WaveCounter>();
        services.AddCustomDispatcher(typeof(DispatcherTests).Assembly);
        await using var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        Assert.Equal("pong:scan", await dispatcher.Send(new Ping("scan")));
        await dispatcher.Publish(new Waved());
        Assert.Equal(2, provider.GetRequiredService<WaveCounter>().Count);
    }
}
