using System.Text;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ExtractionService;

public sealed class ExtractionRequestConsumer(
    ExtractionDatabase database,
    IOptions<RabbitMqOptions> options,
    ILogger<ExtractionRequestConsumer> logger) : BackgroundService
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
            DispatchConsumersAsync = true,
            ClientProvidedName = "ExtractionService"
        };

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
        channel.QueueDeclare(settings.RequestQueue, durable: true, exclusive: false, autoDelete: false, arguments: queueArguments);
        channel.QueueBind(settings.RequestQueue, settings.Exchange, "document.task.extraction.requested");
        channel.BasicQos(0, prefetchCount: 1, global: false);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.Received += async (_, delivery) =>
        {
            try
            {
                var request = ExtractionRequest.Parse(delivery.Body, delivery.BasicProperties.MessageId);
                var inserted = await database.StoreRequestAsync(request, stoppingToken);
                channel.BasicAck(delivery.DeliveryTag, multiple: false);
                logger.LogInformation(
                    "Stored extraction request {TaskId} for document {DocumentId}; newRequest={NewRequest}.",
                    request.TaskId,
                    request.DocumentId,
                    inserted);
            }
            catch (InvalidDataException exception)
            {
                logger.LogError(exception, "Rejecting invalid extraction request {MessageId}.", delivery.BasicProperties.MessageId);
                channel.BasicNack(delivery.DeliveryTag, multiple: false, requeue: false);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Could not persist extraction request {MessageId}; requeueing.", delivery.BasicProperties.MessageId);
                channel.BasicNack(delivery.DeliveryTag, multiple: false, requeue: true);
            }
        };

        channel.BasicConsume(settings.RequestQueue, autoAck: false, consumer);
        logger.LogInformation("Consuming extraction requests from RabbitMQ queue {Queue}.", settings.RequestQueue);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}