using System.Text;
using DocuManagementApp.Models;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace DocuManagementApp.Services;

public sealed class OutboxPublisherOptions
{
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string VirtualHost { get; set; } = "/";
    public string Exchange { get; set; } = "documents.events";
    public string Queue { get; set; } = "documents.ingested";
    public string RoutingKey { get; set; } = "document.ingested";
    public int BatchSize { get; set; } = 20;
    public int PollIntervalSeconds { get; set; } = 2;
    public int LeaseSeconds { get; set; } = 60;
    public int ConfirmTimeoutSeconds { get; set; } = 10;
    public int RetryBaseSeconds { get; set; } = 2;
    public int RetryMaxSeconds { get; set; } = 300;
}

public interface IOutboxEventPublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken);
}

public sealed class RabbitMqOutboxEventPublisher : IOutboxEventPublisher
{
    private readonly OutboxPublisherOptions _options;
    private readonly ILogger<RabbitMqOutboxEventPublisher> _logger;

    public RabbitMqOutboxEventPublisher(
        IOptions<OutboxPublisherOptions> options,
        ILogger<RabbitMqOutboxEventPublisher> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password,
            VirtualHost = _options.VirtualHost,
            AutomaticRecoveryEnabled = true,
            ClientProvidedName = "DocuManagementApp-outbox-publisher"
        };

        using var connection = factory.CreateConnection();
        using var channel = connection.CreateModel();
        channel.ExchangeDeclare(_options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false);
        channel.QueueDeclare(_options.Queue, durable: true, exclusive: false, autoDelete: false);
        channel.QueueBind(_options.Queue, _options.Exchange, _options.RoutingKey);
        channel.ConfirmSelect();

        var properties = channel.CreateBasicProperties();
        properties.MessageId = message.Id.ToString("D");
        properties.Type = message.EventType;
        properties.ContentType = "application/json";
        properties.ContentEncoding = "utf-8";
        properties.DeliveryMode = 2;
        properties.Timestamp = new AmqpTimestamp(message.CreatedAtUtc.ToUnixTimeSeconds());
        properties.Headers = new Dictionary<string, object>
        {
            ["schemaVersion"] = message.SchemaVersion,
            ["documentId"] = message.DocumentId.ToString("D")
        };

        var body = Encoding.UTF8.GetBytes(message.Payload);
        channel.BasicPublish(_options.Exchange, _options.RoutingKey, mandatory: true, basicProperties: properties, body: body);
        channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(_options.ConfirmTimeoutSeconds));
        _logger.LogInformation("RabbitMQ confirmed outbox event {EventId} ({EventType}).", message.Id, message.EventType);
        return Task.CompletedTask;
    }
}

public sealed class OutboxPublisherService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOutboxEventPublisher _publisher;
    private readonly OutboxPublisherOptions _options;
    private readonly ILogger<OutboxPublisherService> _logger;

    public OutboxPublisherService(
        IServiceScopeFactory scopeFactory,
        IOutboxEventPublisher publisher,
        IOptions<OutboxPublisherOptions> options,
        ILogger<OutboxPublisherService> logger)
    {
        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var publishedCount = await PublishBatchAsync(stoppingToken);
                if (publishedCount == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Outbox publisher batch failed; unpublished events remain available for retry.");
                await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken);
            }
        }
    }

    public async Task<int> PublishBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var messages = await store.ClaimDueAsync(
            _options.BatchSize,
            TimeSpan.FromSeconds(_options.LeaseSeconds),
            cancellationToken);

        var publishedCount = 0;
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _publisher.PublishAsync(message, cancellationToken);
                await store.MarkPublishedAsync(message.Id, message.LockToken!.Value, cancellationToken);
                publishedCount++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var retryDelay = CalculateRetryDelay(message.AttemptCount, _options.RetryBaseSeconds, _options.RetryMaxSeconds);
                var nextAttemptAt = DateTimeOffset.UtcNow.Add(retryDelay);
                try
                {
                    await store.MarkFailedAsync(
                        message.Id,
                        message.LockToken!.Value,
                        exception.ToString(),
                        nextAttemptAt,
                        cancellationToken);
                    _logger.LogWarning(exception,
                        "Publishing outbox event {EventId} failed on attempt {AttemptCount}; it will be retried at {NextAttemptAtUtc}.",
                        message.Id,
                        message.AttemptCount,
                        nextAttemptAt);
                }
                catch (Exception updateException) when (updateException is not OperationCanceledException)
                {
                    _logger.LogError(updateException,
                        "Failed to persist retry metadata for outbox event {EventId}; its lease will expire and allow a retry.",
                        message.Id);
                }
            }
        }

        return publishedCount;
    }

    public static TimeSpan CalculateRetryDelay(int attemptCount, int baseSeconds, int maxSeconds)
    {
        if (baseSeconds <= 0 || maxSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baseSeconds), "Retry delays must be positive.");
        }

        var seconds = (double)baseSeconds * Math.Pow(2, Math.Clamp(attemptCount - 1, 0, 30));
        return TimeSpan.FromSeconds(Math.Min(seconds, maxSeconds));
    }
}
