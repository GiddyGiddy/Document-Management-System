using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace SearchService;

public sealed class SearchOutboxPublisher(
    SearchDatabase database,
    IOptions<SearchOptions> options,
    ILogger<SearchOutboxPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value.RabbitMq;
        var factory = new ConnectionFactory
        {
            HostName = settings.HostName,
            Port = settings.Port,
            VirtualHost = settings.VirtualHost,
            UserName = settings.UserName,
            Password = settings.Password,
            AutomaticRecoveryEnabled = true,
            ClientProvidedName = "SearchService-outbox"
        };
        using var connection = factory.CreateConnection();
        using var channel = connection.CreateModel();
        channel.ExchangeDeclare(settings.Exchange, ExchangeType.Topic, durable: true, autoDelete: false);
        channel.ConfirmSelect();
        var returnedIds = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        channel.BasicReturn += (_, returned) =>
        {
            if (!string.IsNullOrWhiteSpace(returned.BasicProperties.MessageId))
            {
                returnedIds.TryAdd(returned.BasicProperties.MessageId, 0);
            }
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var messages = await database.ClaimOutboxAsync(20, TimeSpan.FromSeconds(settings.LeaseSeconds), stoppingToken);
                if (messages.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(settings.PollIntervalSeconds), stoppingToken);
                    continue;
                }

                foreach (var message in messages)
                {
                    try
                    {
                        Publish(channel, settings, message, returnedIds);
                        await database.MarkOutboxPublishedAsync(message, stoppingToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                    {
                        var delaySeconds = Math.Min(settings.RetryMaxSeconds,
                            settings.RetryBaseSeconds * Math.Pow(2, Math.Clamp(message.AttemptCount, 0, 20)));
                        await database.MarkOutboxFailedAsync(message, TimeSpan.FromSeconds(delaySeconds), stoppingToken);
                        logger.LogWarning(exception, "Publishing search completion {EventId} failed; retrying later.", message.EventId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Search outbox polling failed.");
                await Task.Delay(TimeSpan.FromSeconds(settings.PollIntervalSeconds), stoppingToken);
            }
        }
    }

    private static void Publish(IModel channel, RabbitMqOptions settings, SearchOutboxEvent message, ConcurrentDictionary<string, byte> returnedIds)
    {
        var messageId = message.EventId.ToString("D");
        returnedIds.TryRemove(messageId, out _);
        var properties = channel.CreateBasicProperties();
        properties.MessageId = messageId;
        properties.CorrelationId = message.CorrelationId.ToString("D");
        properties.Type = "document.task.completed";
        properties.ContentType = "application/json";
        properties.ContentEncoding = Encoding.UTF8.WebName;
        properties.DeliveryMode = 2;
        properties.Headers = new Dictionary<string, object> { ["schemaVersion"] = 1, ["documentId"] = message.DocumentId.ToString("D") };
        channel.BasicPublish(settings.Exchange, message.RoutingKey, mandatory: true, basicProperties: properties,
            body: Encoding.UTF8.GetBytes(message.Payload));
        channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(settings.ConfirmTimeoutSeconds));
        if (returnedIds.TryRemove(messageId, out _))
        {
            throw new IOException($"No queue is bound for Search Service result route '{message.RoutingKey}'.");
        }
    }
}