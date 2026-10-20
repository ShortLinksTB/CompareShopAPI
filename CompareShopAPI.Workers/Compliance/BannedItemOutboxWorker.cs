using System.Text;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace CompareShopAPI.Workers.Compliance;

public interface IBannedItemMessagePublisher
{
    Task Publish(BannedItemOutboxMessage message, CancellationToken ct);
}

public sealed class RabbitMqBannedItemMessagePublisher : IBannedItemMessagePublisher, IAsyncDisposable
{
    private readonly IConfiguration configuration;
    private readonly ILogger<RabbitMqBannedItemMessagePublisher> logger;
    private readonly Func<string, CancellationToken, Task<IConnection>>? connectionFactory;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IConnection? connection;
    private bool disposed;

    public RabbitMqBannedItemMessagePublisher(IConfiguration configuration, ILogger<RabbitMqBannedItemMessagePublisher> logger)
        : this(configuration, logger, null) { }

    internal RabbitMqBannedItemMessagePublisher(IConfiguration configuration, ILogger<RabbitMqBannedItemMessagePublisher> logger,
        Func<string, CancellationToken, Task<IConnection>>? connectionFactory)
    {
        this.configuration = configuration;
        this.logger = logger;
        this.connectionFactory = connectionFactory;
    }

    public async Task Publish(BannedItemOutboxMessage message, CancellationToken ct)
    {
        var host = configuration["RabbitMQ:Host"];
        if (string.IsNullOrWhiteSpace(host)) throw new InvalidOperationException("RabbitMQ host is not configured.");

        await gate.WaitAsync(ct);
        try
        {
            connection ??= connectionFactory is null ? await CreateConnection(host, ct) : await connectionFactory(host, ct);
            await using var channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true, null, null), ct);
            var queue = $"{configuration["RabbitMQ:QueuePrefix"] ?? "compareshop."}banned-items.events";
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, arguments: null, cancellationToken: ct);
            var properties = new BasicProperties
            {
                Persistent = true,
                MessageId = message.Id.ToString("D"),
                Type = message.EventType,
                ContentType = "application/json"
            };
            await channel.BasicPublishAsync(string.Empty, queue, mandatory: true, properties,
                Encoding.UTF8.GetBytes(message.Payload), ct);
        }
        catch
        {
            if (connection is { IsOpen: false })
            {
                await connection.DisposeAsync();
                connection = null;
            }
            throw;
        }
        finally { gate.Release(); }
    }

    private async Task<IConnection> CreateConnection(string host, CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
            RequestedConnectionTimeout = TimeSpan.FromSeconds(2)
        };
        if (Uri.TryCreate(host, UriKind.Absolute, out var uri) && uri.Scheme is "amqp" or "amqps") factory.Uri = uri;
        else factory.HostName = host;
        logger.LogInformation("Connecting compliance outbox publisher to configured RabbitMQ host.");
        return await factory.CreateConnectionAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        if (connection is not null) await connection.DisposeAsync();
        gate.Dispose();
    }
}

public sealed class BannedItemOutboxDispatcher(CompareShopDbContext db, IBannedItemMessagePublisher publisher,
    TimeProvider clock, ILogger<BannedItemOutboxDispatcher> logger)
{
    public async Task<int> DispatchBatch(CancellationToken ct)
    {
        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var pending = await db.BannedItemOutboxMessages.Where(message => message.PublishedAtUtc == null && message.NextAttemptAtUtc <= nowUtc)
            .OrderBy(message => message.CreatedAtUtc).ThenBy(message => message.Id).Take(20).ToListAsync(ct);
        foreach (var message in pending)
        {
            try
            {
                await publisher.Publish(message, ct);
                message.PublishedAtUtc = clock.GetUtcNow().UtcDateTime;
                message.LastError = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                message.ScheduleRetry(clock.GetUtcNow().UtcDateTime, exception.GetType().Name);
                logger.LogWarning("Could not publish compliance outbox event {EventId}; retry scheduled (attempt {AttemptCount}, {ErrorType}).",
                    message.Id, message.AttemptCount, exception.GetType().Name);
            }
            await db.SaveChangesAsync(ct);
        }
        return pending.Count;
    }
}

public sealed class BannedItemOutboxWorker(IServiceScopeFactory scopes, ILogger<BannedItemOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int processed;
                await using (var scope = scopes.CreateAsyncScope())
                    processed = await scope.ServiceProvider.GetRequiredService<BannedItemOutboxDispatcher>().DispatchBatch(stoppingToken);
                if (processed == 0) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError("Compliance outbox polling failed ({ErrorType}); the worker will retry.", exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
