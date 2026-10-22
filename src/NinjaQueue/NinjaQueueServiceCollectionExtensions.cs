using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NinjaQueue.Enqueue;
using NinjaQueue.Execution;
using NinjaQueue.Registry;
using NinjaQueue.Serialization;

namespace NinjaQueue;

/// <summary>Registers NinjaQueue (§3.1).</summary>
public static class NinjaQueueServiceCollectionExtensions
{
    /// <summary>
    /// Adds the engine. Bind <c>NinjaQueue</c> from <paramref name="configuration"/> (optional), then register handlers and a
    /// storage provider on the returned builder:
    /// <code>services.AddNinjaQueue(configuration).UseEfCore&lt;AppDbContext&gt;().AddJobHandlersFromAssembly(assembly);</code>
    /// The registry is validated when the host starts; an invalid declared contract refuses the start.
    /// </summary>
    public static NinjaQueueBuilder AddNinjaQueue(this IServiceCollection services, IConfiguration? configuration = null, Action<NinjaQueueOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        OptionsBuilder<NinjaQueueOptions> options = services.AddOptions<NinjaQueueOptions>();
        if (configuration is not null)
        {
            options.Bind(configuration.GetSection(NinjaQueueOptions.SectionName));
        }

        if (configure is not null)
        {
            options.Configure(configure);
        }

        NinjaQueueBuilder builder = new(services);

        // The engine's own job: filter-based bulk admin operations (§17.9).
        builder.AddJobHandler<Admin.BulkOperationJobHandler>();
        services.TryAddSingleton(sp => JobHandlerRegistry.Build(builder.HandlerTypes, builder.DeadLetterHandlerTypes, sp.GetRequiredService<IOptions<NinjaQueueOptions>>().Value));
        services.TryAddSingleton(sp => new JobPayloadSerializer(sp.GetRequiredService<IOptions<NinjaQueueOptions>>().Value.MaxPayloadBytes));
        services.TryAddSingleton<IJobSignal, InProcessJobSignal>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IJobEnqueuer, JobEnqueuer>();
        services.TryAddSingleton(sp => new Observability.JobMetrics(sp.GetService<System.Diagnostics.Metrics.IMeterFactory>()));
        services.TryAddSingleton<IRetryClassifier, DefaultRetryClassifier>();
        services.TryAddSingleton<JobExecutor>();
        services.TryAddSingleton<DeadLetterDispatcher>();

        services.TryAddScoped<Admin.JobAdminService>();
        services.TryAddScoped<Admin.IJobAdminService>(sp => sp.GetRequiredService<Admin.JobAdminService>());
        services.TryAddSingleton<Hosting.NinjaQueueHealthCheck>();

        // The single shutdown budget needs 30 s (§16.2) unless the host chose a longer one.
        services.Configure<HostOptions>(o =>
        {
            if (o.ShutdownTimeout < TimeSpan.FromSeconds(30))
            {
                o.ShutdownTimeout = TimeSpan.FromSeconds(30);
            }
        });

        // Validates the registry before anything else starts (an invalid contract refuses the host start), then runs the
        // startup checks, queue workers, heartbeat and maintenance (workers only when NinjaQueue:Worker:Enabled).
        services.Insert(0, ServiceDescriptor.Singleton<IHostedService, NinjaQueueStartupValidator>());
        services.TryAddSingleton<Hosting.NinjaQueueWorkerHost>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<Hosting.NinjaQueueWorkerHost>());
        return builder;
    }
}

/// <summary>Registers job handlers and the storage provider.</summary>
public sealed class NinjaQueueBuilder
{
    private readonly List<Type> _handlerTypes = [];
    private readonly List<Type> _deadLetterHandlerTypes = [];

    internal NinjaQueueBuilder(IServiceCollection services) => Services = services;

    /// <summary>The service collection.</summary>
    public IServiceCollection Services { get; }

    /// <summary>The registered handler types.</summary>
    public IReadOnlyList<Type> HandlerTypes => _handlerTypes;

    /// <summary>The dead-letter handler classes registered as their own services (not on a job handler class).</summary>
    public IReadOnlyList<Type> DeadLetterHandlerTypes => _deadLetterHandlerTypes;

    /// <summary>Registers one handler (scoped).</summary>
    public NinjaQueueBuilder AddJobHandler<THandler>()
        where THandler : class
        => AddJobHandler(typeof(THandler));

    /// <summary>Registers one handler type (scoped).</summary>
    public NinjaQueueBuilder AddJobHandler(Type handlerType)
    {
        ArgumentNullException.ThrowIfNull(handlerType);
        if (!_handlerTypes.Contains(handlerType))
        {
            _handlerTypes.Add(handlerType);
            Services.TryAddScoped(handlerType);
        }

        return this;
    }

    /// <summary>
    /// Registers a class implementing <see cref="IJobDeadLetterHandler{TPayload}"/> as its own scoped service. A job handler
    /// class that implements the interface for its own payload needs no extra registration.
    /// </summary>
    public NinjaQueueBuilder AddDeadLetterHandler<THandler>()
        where THandler : class
        => AddDeadLetterHandler(typeof(THandler));

    /// <summary>Registers a dead-letter handler type (scoped).</summary>
    public NinjaQueueBuilder AddDeadLetterHandler(Type handlerType)
    {
        ArgumentNullException.ThrowIfNull(handlerType);
        if (!_deadLetterHandlerTypes.Contains(handlerType))
        {
            _deadLetterHandlerTypes.Add(handlerType);
            Services.TryAddScoped(handlerType);
        }

        return this;
    }

    /// <summary>
    /// Registers every class in <paramref name="assembly"/> that carries <see cref="JobTypeAttribute"/>, and every other
    /// concrete class that implements <see cref="IJobDeadLetterHandler{TPayload}"/>.
    /// </summary>
    public NinjaQueueBuilder AddJobHandlersFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (Type type in assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition))
        {
            if (type.GetCustomAttribute<JobTypeAttribute>() is not null)
            {
                AddJobHandler(type);
            }
            else if (type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IJobDeadLetterHandler<>)))
            {
                AddDeadLetterHandler(type);
            }
        }

        return this;
    }
}

/// <summary>Builds (and so validates) the registry at host start.</summary>
internal sealed class NinjaQueueStartupValidator(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = services.GetRequiredService<JobHandlerRegistry>();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}