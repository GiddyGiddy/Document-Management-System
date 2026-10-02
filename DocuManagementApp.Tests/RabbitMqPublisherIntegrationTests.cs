using DocuManagementApp.Models;
using DocuManagementApp.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace DocuManagementApp.Tests;

public sealed class RabbitMqPublisherIntegrationFactAttribute : FactAttribute
{
    public RabbitMqPublisherIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OUTBOX_TEST_RABBITMQ_USERNAME"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OUTBOX_TEST_RABBITMQ_PASSWORD")))
        {
            Skip = "Set OUTBOX_TEST_RABBITMQ_USERNAME and OUTBOX_TEST_RABBITMQ_PASSWORD to run the live RabbitMQ integration test.";
        }
    }
}

public sealed class RabbitMqPublisherIntegrationTests
{
    [RabbitMqPublisherIntegrationFact]
    public async Task PublisherConnectsPublishesAndReceivesBrokerConfirmation()
    {
        var options = new OutboxPublisherOptions
        {
            HostName = Environment.GetEnvironmentVariable("OUTBOX_TEST_RABBITMQ_HOST") ?? "localhost",
            Port = int.TryParse(Environment.GetEnvironmentVariable("OUTBOX_TEST_RABBITMQ_PORT"), out var port) ? port : 5672,
            UserName = Environment.GetEnvironmentVariable("OUTBOX_TEST_RABBITMQ_USERNAME")!,
            Password = Environment.GetEnvironmentVariable("OUTBOX_TEST_RABBITMQ_PASSWORD")!,
            VirtualHost = Environment.GetEnvironmentVariable("OUTBOX_TEST_RABBITMQ_VHOST") ?? "/",
            Exchange = $"outbox.integration.{Guid.NewGuid():N}",
            Queue = $"outbox.integration.{Guid.NewGuid():N}",
            RoutingKey = "document.ingested",
            ConfirmTimeoutSeconds = 10
        };
        var publisher = new RabbitMqOutboxEventPublisher(
            Options.Create(options),
            NullLogger<RabbitMqOutboxEventPublisher>.Instance);
        var correlationId = Guid.NewGuid();
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            CorrelationId = correlationId,
            DocumentId = Guid.NewGuid(),
            EventType = "document.ingested",
            SchemaVersion = 1,
            Payload = $"{{\"eventType\":\"document.ingested\",\"schemaVersion\":1,\"correlation_id\":\"{correlationId:D}\",\"processingStatus\":\"Completed\",\"requestedOutputFormat\":\"Original\"}}",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        try
        {
            await publisher.PublishAsync(message, CancellationToken.None);
            var factory = new ConnectionFactory
            {
                HostName = options.HostName,
                Port = options.Port,
                UserName = options.UserName,
                Password = options.Password,
                VirtualHost = options.VirtualHost
            };
            using var connection = factory.CreateConnection();
            using var channel = connection.CreateModel();
            var delivery = channel.BasicGet(options.Queue, autoAck: true);
            Assert.NotNull(delivery);
            Assert.Equal(message.Id.ToString("D"), delivery.BasicProperties.MessageId);
            Assert.Equal(message.CorrelationId.ToString("D"), delivery.BasicProperties.CorrelationId);
            Assert.Equal(message.SchemaVersion, delivery.BasicProperties.Headers["schemaVersion"]);
            Assert.Equal(message.DocumentId.ToString("D"), delivery.BasicProperties.Headers["documentId"]);
            Assert.Equal(message.CorrelationId.ToString("D"), delivery.BasicProperties.Headers["correlation_id"]);
            Assert.Equal("Completed", delivery.BasicProperties.Headers["processingStatus"]);
            Assert.Equal("Original", delivery.BasicProperties.Headers["requestedOutputFormat"]);
            var payload = System.Text.Encoding.UTF8.GetString(delivery.Body.ToArray());
            Assert.Contains($"\"correlation_id\":\"{correlationId:D}\"", payload);
            Assert.Contains("\"processingStatus\":\"Completed\"", payload);
            Assert.Contains("\"requestedOutputFormat\":\"Original\"", payload);
        }
        finally
        {
            var factory = new ConnectionFactory
            {
                HostName = options.HostName,
                Port = options.Port,
                UserName = options.UserName,
                Password = options.Password,
                VirtualHost = options.VirtualHost
            };
            using var connection = factory.CreateConnection();
            using var channel = connection.CreateModel();
            channel.QueueDelete(options.Queue);
            channel.ExchangeDelete(options.Exchange);
        }
    }
}
