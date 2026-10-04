using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace ExtractionService;

public sealed class ExtractionOutboxPublisherService(
    ExtractionDatabase database,
    IOptions<RabbitMqOptions> options,
    ILogger<ExtractionOutboxPublisherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var factory = new ConnectionFactory
        {
            HostName = settings.HostName,
            Port = settings.Port,
            VirtualHost = settings.VirtualHost,
            UserName = settings.UserName,
            Password = settings.Password,
            AutomaticRecoveryEnabled = true,
            ClientProvidedName = "ExtractionService-outbox"
        };

        using var connection = factory.CreateConnection();
        using var channel = connection.CreateModel();
        channel.ExchangeDeclare(settings.Exchange, ExchangeType.Topic, durable: true, autoDelete: false);
        channel.ConfirmSelect();
        var returnedEventIds = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        channel.BasicReturn += (_, returned) =>
        {
            if (!string.IsNullOrWhiteSpace(returned.BasicProperties.MessageId))
            {
                returnedEventIds.TryAdd(returned.BasicProperties.MessageId, 0);
            }
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var events = await database.ClaimOutboxAsync(
                    batchSize: 20,
                    TimeSpan.FromSeconds(settings.LeaseSeconds),
                    stoppingToken);
                if (events.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(settings.PollIntervalSeconds), stoppingToken);
                    continue;
                }

                foreach (var message in events)
                {
                    try
                    {
                        Publish(channel, settings, message, returnedEventIds);
                        await database.MarkOutboxPublishedAsync(message, stoppingToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                    {
                        var delay = CalculateRetryDelay(message.AttemptCount, settings.RetryBaseSeconds, settings.RetryMaxSeconds);
                        await database.MarkOutboxFailedAsync(message, delay, stoppingToken);
                        logger.LogWarning(exception,
                            "Publishing extraction outcome {EventId} failed; retrying after {RetryDelay}.",
                            message.EventId,
                            delay);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Extraction outbox polling failed.");
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
        RabbitMqOptions settings,
        ExtractionOutboxEvent message,
        ConcurrentDictionary<string, byte> returnedEventIds)
    {
        var messageId = message.EventId.ToString("D");
        returnedEventIds.TryRemove(messageId, out _);
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
            settings.Exchange,
            message.RoutingKey,
            mandatory: true,
            basicProperties: properties,
            body: Encoding.UTF8.GetBytes(message.Payload));
        channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(settings.ConfirmTimeoutSeconds));
        if (returnedEventIds.TryRemove(messageId, out _))
        {
            throw new IOException($"No queue is bound for extraction outcome route '{message.RoutingKey}'.");
        }
    }
}