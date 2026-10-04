using System.Text;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OrchestratorService;

public sealed class RabbitMqConsumerService(
    OrchestratorDatabase database,
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqConsumerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var factory = CreateConnectionFactory(settings);
        factory.DispatchConsumersAsync = true;
        using var connection = factory.CreateConnection();
        using var channel = connection.CreateModel();

        channel.ExchangeDeclare(settings.Exchange, ExchangeType.Topic, durable: true, autoDelete: false);
        channel.ExchangeDeclare(settings.DeadLetterExchange, ExchangeType.Fanout, durable: true, autoDelete: false);
        channel.QueueDeclare(settings.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false);
        channel.QueueBind(settings.DeadLetterQueue, settings.DeadLetterExchange, routingKey: string.Empty);

        var queueArguments = new Dictionary<string, object>
        {
            ["x-dead-letter-exchange"] = settings.DeadLetterExchange
        };
        channel.QueueDeclare(settings.IngestionQueue, durable: true, exclusive: false, autoDelete: false, arguments: queueArguments);
        channel.QueueBind(settings.IngestionQueue, settings.Exchange, "document.ingested");
        channel.QueueBind(settings.IngestionQueue, settings.Exchange, "document.task.completed");
        channel.QueueBind(settings.IngestionQueue, settings.Exchange, "document.task.failed");
        channel.BasicQos(0, prefetchCount: 1, global: false);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.Received += async (_, delivery) =>
        {
            try
            {
                var message = OrchestrationEvent.Parse(
                    delivery.Body,
                    delivery.BasicProperties.MessageId,
                    delivery.BasicProperties.Type);
                var processed = await database.ProcessAsync(message, stoppingToken);
                channel.BasicAck(delivery.DeliveryTag, multiple: false);
                logger.LogInformation(
                    "Handled orchestration event {EventType} {EventId}; processed={Processed}.",
                    message.EventType,
                    message.EventId,
                    processed);
            }
            catch (InvalidDataException exception)
            {
                logger.LogError(exception, "Rejecting invalid orchestration event {MessageId}.", delivery.BasicProperties.MessageId);
                channel.BasicNack(delivery.DeliveryTag, multiple: false, requeue: false);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Orchestration event {MessageId} failed and will be requeued.", delivery.BasicProperties.MessageId);
                channel.BasicNack(delivery.DeliveryTag, multiple: false, requeue: true);
            }
        };

        channel.BasicConsume(settings.IngestionQueue, autoAck: false, consumer);
        logger.LogInformation("Consuming document lifecycle events from RabbitMQ queue {Queue}.", settings.IngestionQueue);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal static ConnectionFactory CreateConnectionFactory(RabbitMqOptions options)
    {
        return new ConnectionFactory
        {
            HostName = options.HostName,
            Port = options.Port,
            VirtualHost = options.VirtualHost,
            UserName = options.UserName,
            Password = options.Password,
            AutomaticRecoveryEnabled = true,
            ClientProvidedName = "OrchestratorService"
        };
    }
}