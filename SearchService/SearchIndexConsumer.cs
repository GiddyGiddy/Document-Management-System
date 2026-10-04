using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace SearchService;

public sealed class SearchIndexConsumer(
    SearchDatabase database,
    IExtractionArtifactClient artifactClient,
    ITextEmbeddingClient embeddingClient,
    IOptions<SearchOptions> options,
    ILogger<SearchIndexConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var factory = new ConnectionFactory
        {
            HostName = settings.RabbitMq.HostName,
            Port = settings.RabbitMq.Port,
            VirtualHost = settings.RabbitMq.VirtualHost,
            UserName = settings.RabbitMq.UserName,
            Password = settings.RabbitMq.Password,
            AutomaticRecoveryEnabled = true,
            DispatchConsumersAsync = true,
            ClientProvidedName = "SearchService-index-consumer"
        };

        using var connection = factory.CreateConnection();
        using var channel = connection.CreateModel();
        channel.ExchangeDeclare(settings.RabbitMq.Exchange, ExchangeType.Topic, durable: true, autoDelete: false);
        channel.ExchangeDeclare(settings.RabbitMq.DeadLetterExchange, ExchangeType.Fanout, durable: true, autoDelete: false);
        channel.QueueDeclare(settings.RabbitMq.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false);
        channel.QueueBind(settings.RabbitMq.DeadLetterQueue, settings.RabbitMq.DeadLetterExchange, routingKey: string.Empty);
        channel.QueueDeclare(settings.RabbitMq.IndexQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object> { ["x-dead-letter-exchange"] = settings.RabbitMq.DeadLetterExchange });
        channel.QueueBind(settings.RabbitMq.IndexQueue, settings.RabbitMq.Exchange, "document.task.search-index.requested");
        channel.BasicQos(0, prefetchCount: 1, global: false);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.Received += async (_, delivery) =>
        {
            try
            {
                var request = SearchIndexCommand.Parse(delivery.Body, delivery.BasicProperties.MessageId);
                var artifact = await artifactClient.GetAsync(request.ArtifactReference, stoppingToken);
                if (artifact.TaskId != request.TaskId || artifact.DocumentId != request.DocumentId ||
                    !string.Equals(artifact.DocumentKind, request.DocumentKind, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The extraction artifact does not match the search-index task.");
                }

                var chunks = SearchChunker.Create(artifact);
                var embeddings = settings.Embeddings.IsConfigured
                    ? await embeddingClient.EmbedAsync(chunks.Select(chunk => chunk.Text).ToArray(), stoppingToken)
                    : null;
                var inserted = await database.IndexAsync(request, artifact, chunks, embeddings, stoppingToken);
                channel.BasicAck(delivery.DeliveryTag, multiple: false);
                logger.LogInformation("Indexed document {DocumentId} as {DocumentKind}; newEvent={NewEvent}, chunks={ChunkCount}.",
                    request.DocumentId, request.DocumentKind, inserted, chunks.Count);
            }
            catch (InvalidDataException exception)
            {
                logger.LogError(exception, "Rejecting invalid search-index request {MessageId}.", delivery.BasicProperties.MessageId);
                channel.BasicNack(delivery.DeliveryTag, multiple: false, requeue: false);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Search indexing failed for {MessageId}; requeueing.", delivery.BasicProperties.MessageId);
                channel.BasicNack(delivery.DeliveryTag, multiple: false, requeue: true);
            }
        };

        channel.BasicConsume(settings.RabbitMq.IndexQueue, autoAck: false, consumer);
        logger.LogInformation("Consuming search-index requests from queue {Queue}.", settings.RabbitMq.IndexQueue);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}