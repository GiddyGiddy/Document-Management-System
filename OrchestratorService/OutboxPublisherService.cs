using System.Text;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using System.Collections.Concurrent;

namespace OrchestratorService;

public sealed class OutboxPublisherService(
    OrchestratorDatabase database,
    IOptions<RabbitMqOptions> options,
    ILogger<OutboxPublisherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var factory = RabbitMqConsumerService.CreateConnectionFactory(settings);
        using var connection = factory.CreateConnection();
        using var channel = connection.CreateModel();
        channel.ExchangeDeclare(settings.Exchange, ExchangeType.Topic, durable: true, autoDelete: false);
        channel.ConfirmSelect();
        var returnedMessageIds = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        channel.BasicReturn += (_, returned) =>
        {
            if (!string.IsNullOrWhiteSpace(returned.BasicProperties.MessageId))
            {
                returnedMessageIds.TryAdd(returned.BasicProperties.MessageId, 0);
            }
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var messages = await database.ClaimOutboxAsync(
                    settings.BatchSize,
                    TimeSpan.FromSeconds(settings.LeaseSeconds),
                    stoppingToken);
                if (messages.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(settings.PollIntervalSeconds), stoppingToken);
                    continue;
                }

                foreach (var message in messages)
                {
                    try
                    {
                        Publish(channel, settings.Exchange, message, settings.ConfirmTimeoutSeconds, returnedMessageIds);
                        await database.MarkOutboxPublishedAsync(message, stoppingToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                    {
                        var retryDelay = CalculateRetryDelay(message.AttemptCount, settings.RetryBaseSeconds, settings.RetryMaxSeconds);
                        await database.MarkOutboxFailedAsync(message, DateTimeOffset.UtcNow.Add(retryDelay), stoppingToken);
                        logger.LogWarning(exception,
                            "Publishing task request {EventId} failed; retrying after {RetryDelay}.",
                            message.EventId,
                            retryDelay);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Orchestrator outbox polling failed.");
                await Task.Delay(TimeSpan.FromSeconds(settings.PollIntervalSeconds), stoppingToken);
            }
        }
    }

    internal static TimeSpan CalculateRetryDelay(int attemptCount, int baseSeconds, int maxSeconds)
    {
        var seconds = (double)baseSeconds * Math.Pow(2, Math.Clamp(attemptCount, 0, 30));
        return TimeSpan.FromSeconds(Math.Min(seconds, maxSeconds));
    }

    private static void Publish(
        IModel channel,
        string exchange,
        OutboxRecord message,
        int confirmTimeoutSeconds,
        ConcurrentDictionary<string, byte> returnedMessageIds)
    {
        var messageId = message.EventId.ToString("D");
        returnedMessageIds.TryRemove(messageId, out _);
        var properties = channel.CreateBasicProperties();
        properties.MessageId = messageId;
        properties.CorrelationId = message.CorrelationId.ToString("D");
        properties.Type = message.EventType;
        properties.ContentType = "application/json";
        properties.ContentEncoding = Encoding.UTF8.WebName;
        properties.DeliveryMode = 2;
        properties.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        properties.Headers = new Dictionary<string, object>
        {
            ["schemaVersion"] = 1,
            ["documentId"] = message.DocumentId.ToString("D")
        };

        channel.BasicPublish(
            exchange: exchange,
            routingKey: message.RoutingKey,
            mandatory: true,
            basicProperties: properties,
            body: Encoding.UTF8.GetBytes(message.Payload));
        channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(confirmTimeoutSeconds));
        if (returnedMessageIds.TryRemove(messageId, out _))
        {
            throw new IOException($"No queue is bound for task route '{message.RoutingKey}'.");
        }
    }
}